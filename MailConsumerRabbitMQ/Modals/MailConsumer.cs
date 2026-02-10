using System.Collections.Concurrent;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using MimeKit;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MailConsumerRabbitMQ.Modals
{
    public class MailConsumer
    {
        private ConnectionFactory _factory;
        private IConnection _connection;
        private IChannel _channel;

        public int sayac = 1;

        // SMTP session cache (host+port+user)
        private readonly ConcurrentDictionary<string, SmtpSession> _smtpSessions = new();

        public async Task InitializeAsync()
        {
            _factory = new ConnectionFactory
            {
                Port = 5672,
                HostName = "c_rabbitmq",
                UserName = "user",
                Password = "1234567",
            };

            _connection = await _factory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            await _channel.QueueDeclareAsync(
                queue: "mail_queue",
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            // ✅ aynı anda çok mesaj çekip aynı SMTP hesabına abanmasın
            await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
        }

        public void StartListening()
        {
            var consumer = new AsyncEventingBasicConsumer(_channel);

            consumer.ReceivedAsync += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                try
                {
                    var mailProperties = JsonConvert.DeserializeObject<MessageMQMail>(message);

                    if (mailProperties != null && mailProperties.MailVM != null)
                    {
                        await SendEmail(mailProperties);
                    }
                    else
                    {
                        var mailMultiple = JsonConvert.DeserializeObject<MessageMQMailMultiple>(message);
                        if (mailMultiple == null)
                            throw new InvalidOperationException("MQ message parse edildi ama null döndü (MessageMQMailMultiple).");

                        await SendEmailMultiple(mailMultiple);
                    }

                    await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Mail gönderme hatası: {ex.Message}");

                    // ❌ requeue:true YOK
                    // ✅ Mesaj kaybolmasın: ACK'leme.
                    // ✅ RabbitMQ unacked mesajı geri alsın diye consumer bağlantısını resetliyoruz.
                    try
                    {
                        if (_channel != null && _channel.IsOpen)
                            await _channel.CloseAsync();

                        if (_connection != null && _connection.IsOpen)
                            await _connection.CloseAsync();
                    }
                    catch { /* ignore */ }

                    // throw: servis/supervisor yeniden başlatıyorsa tekrar Initialize + Consume olur
                    throw;
                }
            };

            _channel.BasicConsumeAsync(
                queue: "mail_queue",
                autoAck: false,
                consumer: consumer);

            Console.WriteLine("MailConsumer çalışıyor, mail kuyruğu dinleniyor...");
        }

        // -------------------------
        // SMTP session wrapper
        // -------------------------
        private sealed class SmtpSession : IAsyncDisposable
        {
            public readonly SmtpClient Client = new();
            public readonly SemaphoreSlim Gate = new(1, 1);

            public bool IsConnected => Client.IsConnected;
            public bool IsAuthed => Client.IsAuthenticated;

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (Client.IsConnected)
                        await Client.DisconnectAsync(true);
                }
                catch { /* ignore */ }

                Client.Dispose();
                Gate.Dispose();
            }
        }

        private string SessionKey(string host, int port, string user)
            => $"{host}:{port}:{user}".ToLowerInvariant();

        // -------------------------
        // SMTP helpers (dinamik TLS)
        // -------------------------
        private static SecureSocketOptions ResolveSocketOptions(string? host, int port, bool externalSslFlag)
        {
            if (port == 465) return SecureSocketOptions.SslOnConnect;
            if (port == 587) return SecureSocketOptions.StartTls;

            if (!string.IsNullOrWhiteSpace(host) &&
                host.Contains("gmail", StringComparison.OrdinalIgnoreCase))
            {
                return externalSslFlag ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            }

            return externalSslFlag ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        }

        private static bool IsGmailTooManyLogin(Exception ex)
        {
            var s = ex.ToString();
            return s.Contains("4.7.0", StringComparison.OrdinalIgnoreCase) &&
                   s.Contains("Too many login attempts", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<SmtpSession> GetOrCreateSessionAsync(string host, int port, bool ssl, string user, string pass)
        {
            var key = SessionKey(host, port, user);

            var session = _smtpSessions.GetOrAdd(key, _ => new SmtpSession());

            // session'ı burada hemen connect etmiyoruz; send içinde lazy yapacağız.
            // ama bozuk session olursa Remove edip yenisini kuracağız.
            return session;
        }

        private async Task EnsureConnectedAndAuthedAsync(SmtpSession session, string host, int port, bool ssl, string user, string pass)
        {
            // Not: Gate ile dışarıdan serialize ediyoruz, burada paralel giriş yok.
            if (!session.Client.IsConnected)
            {
                session.Client.CheckCertificateRevocation = true;
                session.Client.Timeout = 60_000;

                var options = ResolveSocketOptions(host, port, ssl);
                await session.Client.ConnectAsync(host, port, options);
            }

            if (!session.Client.IsAuthenticated)
            {
                await session.Client.AuthenticateAsync(user, pass);
            }
        }

        private async Task SendWithSessionRetryAsync(
            string host, int port, bool ssl, string user, string pass,
            MimeMessage mimeMessage,
            string logPrefix)
        {
            var session = await GetOrCreateSessionAsync(host, port, ssl, user, pass);
            var key = SessionKey(host, port, user);

            // kısa retry: aynı session ile dene; gerekiyorsa resetle
            var delaysMs = new[] { 0, 1000, 2000, 4000 }; // 4 deneme

            for (int attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                if (delaysMs[attempt] > 0)
                    await Task.Delay(delaysMs[attempt]);

                await session.Gate.WaitAsync();
                try
                {
                    // bağlı mı / authed mi kontrol et, değilse bağlan
                    await EnsureConnectedAndAuthedAsync(session, host, port, ssl, user, pass);

                    await session.Client.SendAsync(mimeMessage);

                    // ✅ başarılı
                    return;
                }
                catch (Exception ex)
                {
                    var gmailThrottle = IsGmailTooManyLogin(ex);

                    Console.WriteLine($"{logPrefix} attempt={attempt + 1}/{delaysMs.Length} err={ex.GetType().Name} msg={ex.Message}");

                    // Gmail throttle / server disconnect gibi durumda session çoğu zaman bozulur.
                    // Session'ı resetleyip bir sonraki attempt'te fresh connect yaptır.
                    if (gmailThrottle || !session.Client.IsConnected)
                    {
                        // session'ı tamamen yenile
                        _smtpSessions.TryRemove(key, out _);

                        try { await session.DisposeAsync(); } catch { /* ignore */ }

                        session = new SmtpSession();
                        _smtpSessions[key] = session;
                    }

                    // son attempt ise throw
                    if (attempt == delaysMs.Length - 1)
                        throw;
                }
                finally
                {
                    session.Gate.Release();
                }
            }
        }

        private static void AddAttachmentsFromMailVm(BodyBuilder bodyBuilder, IList<IFormFile>? files)
        {
            if (files == null || files.Count == 0) return;

            foreach (var file in files)
            {
                if (file == null || file.Length <= 0) continue;

                using var ms = new MemoryStream();
                file.CopyTo(ms);
                bodyBuilder.Attachments.Add(file.FileName, ms.ToArray());
            }
        }

        private static void FillFilesFromBase64IfAny(MessageMQMail mail)
        {
            if (mail.fileByteArrays == null || mail.fileByteArrays.Count == 0) return;

            mail.MailVM.Files = new List<IFormFile>();

            for (int i = 0; i < mail.fileByteArrays.Count; i++)
            {
                var base64File = mail.fileByteArrays[i];
                var fileFullName = mail.fileFullName[i];

                var fileBytes = Convert.FromBase64String(base64File);
                var stream = new MemoryStream(fileBytes);

                var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName)
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/octet-stream"
                };

                mail.MailVM.Files.Add(file);
            }
        }

        private static void FillFilesFromBase64IfAny(MessageMQMailMultiple mail)
        {
            if (mail.fileByteArrays == null || mail.fileByteArrays.Count == 0) return;

            if (mail.MailVM == null) mail.MailVM = new MailVM();
            mail.MailVM.Files = new List<IFormFile>();

            for (int i = 0; i < mail.fileByteArrays.Count; i++)
            {
                var base64File = mail.fileByteArrays[i];
                var fileFullName = mail.fileFullName[i];

                var fileBytes = Convert.FromBase64String(base64File);
                var stream = new MemoryStream(fileBytes);

                var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName)
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/octet-stream"
                };

                mail.MailVM.Files.Add(file);
            }
        }

        // -------------------------
        // SendEmail (single)
        // -------------------------
        private async Task SendEmail(MessageMQMail _mail)
        {
            static string OneLine(string? s, int max = 160)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
                if (s.Length > max) s = s.Substring(0, max) + "...";
                return s;
            }
            static string Safe(string? s, int max = 120) => OneLine(s, max);

            var started = DateTime.Now;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var to = Safe(_mail?.MailVM?.To, 200);
            var subject = Safe(_mail?.MailVM?.Subject, 200);

            int attCount = 0;
            long attBytes = 0;

            try
            {
                var mimeMessage = new MimeMessage();
                mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));
                mimeMessage.To.Add(MailboxAddress.Parse($"{_mail.MailVM.BoxName} <{_mail.MailVM.To}>"));
                mimeMessage.Subject = _mail.MailVM.Subject;

                var bodyBuilder = new BodyBuilder { HtmlBody = _mail.MailVM.Body };

                FillFilesFromBase64IfAny(_mail);
                if (_mail.MailVM.Files != null)
                {
                    attCount = _mail.MailVM.Files.Count;
                    foreach (var f in _mail.MailVM.Files) attBytes += f?.Length ?? 0;
                }

                AddAttachmentsFromMailVm(bodyBuilder, _mail.MailVM.Files);
                mimeMessage.Body = bodyBuilder.ToMessageBody();

                var host = _mail.ExternalSmpt;
                var port = _mail.ExternalPort;
                var ssl = _mail.ExternalSsl;

                await SendWithSessionRetryAsync(
                    host, port, ssl,
                    _mail.ExternalMailAddress, _mail.ExternalConnectionKey,
                    mimeMessage,
                    logPrefix: $"[SMTP-SINGLE] to={to} sub=\"{subject}\"");

                sw.Stop();

                Console.WriteLine(
                    $"[Success] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"to={to} sub=\"{subject}\" smtp={host} port={port} ssl={ssl} att={attCount} bytes={attBytes}"
                );

                sayac++;
            }
            catch
            {
                sw.Stop();
                Console.WriteLine(
                    $"[Fail] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"to={to} sub=\"{subject}\" smtp={_mail.ExternalSmpt} port={_mail.ExternalPort} ssl={_mail.ExternalSsl} " +
                    $"att={attCount} bytes={attBytes}"
                );
                throw;
            }
        }

        // -------------------------
        // SendEmail (multiple)
        // -------------------------
        private async Task SendEmailMultiple(MessageMQMailMultiple _mail)
        {
            static string OneLine(string? s, int max = 160)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
                if (s.Length > max) s = s.Substring(0, max) + "...";
                return s;
            }
            static string Safe(string? s, int max = 120) => OneLine(s, max);

            var started = DateTime.Now;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var subject = Safe(_mail?.MailMultiVM?.Subject, 200);

            int attCount = 0;
            long attBytes = 0;

            int toCount = _mail?.MailMultiVM?.ToMultipleBoxAdress?.Count ?? 0;
            string toList = "";
            if (_mail?.MailMultiVM?.ToMultipleBoxAdress != null && _mail.MailMultiVM.ToMultipleBoxAdress.Count > 0)
            {
                toList = string.Join(",", _mail.MailMultiVM.ToMultipleBoxAdress.Select(x => x.Item2));
                toList = Safe(toList, 260);
            }

            try
            {
                var mimeMessage = new MimeMessage();
                mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));

                foreach (var item in _mail.MailMultiVM.ToMultipleBoxAdress)
                    mimeMessage.To.Add(MailboxAddress.Parse($"{item.Item1} <{item.Item2}>"));

                mimeMessage.Subject = _mail.MailMultiVM.Subject;

                var bodyBuilder = new BodyBuilder { HtmlBody = _mail.MailMultiVM.Body };

                FillFilesFromBase64IfAny(_mail);

                // base64'tan üretilen dosyalar
                AddAttachmentsFromMailVm(bodyBuilder, _mail.MailVM?.Files);

                // MultiVM dosyaları
                if (_mail.MailMultiVM.Files != null && _mail.MailMultiVM.Files.Count > 0)
                    AddAttachmentsFromMailVm(bodyBuilder, _mail.MailMultiVM.Files);

                // sayım
                if (_mail.MailVM?.Files != null) { attCount += _mail.MailVM.Files.Count; foreach (var f in _mail.MailVM.Files) attBytes += f?.Length ?? 0; }
                if (_mail.MailMultiVM?.Files != null) { attCount += _mail.MailMultiVM.Files.Count; foreach (var f in _mail.MailMultiVM.Files) attBytes += f?.Length ?? 0; }

                mimeMessage.Body = bodyBuilder.ToMessageBody();

                var host = _mail.ExternalSmpt;
                var port = _mail.ExternalPort;
                var ssl = _mail.ExternalSsl;

                await SendWithSessionRetryAsync(
                    host, port, ssl,
                    _mail.ExternalMailAddress, _mail.ExternalConnectionKey,
                    mimeMessage,
                    logPrefix: $"[SMTP-MULTI] toCount={toCount} to={toList} sub=\"{subject}\"");

                sw.Stop();

                Console.WriteLine(
                    $"[Success] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"toCount={toCount} to={toList} sub=\"{subject}\" smtp={host} port={port} ssl={ssl} att={attCount} bytes={attBytes}"
                );

                sayac++;
            }
            catch
            {
                sw.Stop();
                Console.WriteLine(
                    $"[Fail] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"toCount={toCount} to={toList} sub=\"{subject}\" smtp={_mail.ExternalSmpt} port={_mail.ExternalPort} ssl={_mail.ExternalSsl} " +
                    $"att={attCount} bytes={attBytes}"
                );
                throw;
            }
        }
    }
}
