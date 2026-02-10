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

        public async Task InitializeAsync()
        {
            _factory = new ConnectionFactory
            {
                Port = 5672,
                HostName = "c_rabbitmq",
                //HostName = "192.168.1.76",
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

            await Task.CompletedTask;
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
                        await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    else
                    {
                        var mailMultiple = JsonConvert.DeserializeObject<MessageMQMailMultiple>(message);
                        if (mailMultiple == null)
                            throw new InvalidOperationException("MQ message parse edildi ama null döndü (MessageMQMailMultiple).");

                        await SendEmailMultiple(mailMultiple);
                        await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Mail gönderme hatası: {ex.Message}");

                    // ✅ Mesajı kaybetme. (Kuyruğu bozmaz, sadece tekrar denersin.)
                    await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                }
            };

            _channel.BasicConsumeAsync(
                queue: "mail_queue",
                autoAck: false,
                consumer: consumer);

            Console.WriteLine("MailConsumer çalışıyor, mail kuyruğu dinleniyor...");
        }

        // -------------------------
        // SMTP helpers (dinamik TLS)
        // -------------------------
        private static SecureSocketOptions ResolveSocketOptions(string? host, int port, bool externalSslFlag)
        {
            // Gmail/SMTP genel doğru kullanım:
            // 465 => SSL-on-connect
            // 587 => STARTTLS
            if (port == 465) return SecureSocketOptions.SslOnConnect;
            if (port == 587) return SecureSocketOptions.StartTls;

            // Gmail özel tolerans
            if (!string.IsNullOrWhiteSpace(host) &&
                host.Contains("gmail", StringComparison.OrdinalIgnoreCase))
            {
                // 25 gibi portlarda StartTlsWhenAvailable daha mantıklı
                return externalSslFlag ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            }

            // Genel fallback: flag true => SslOnConnect, değilse StartTlsWhenAvailable
            return externalSslFlag ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        }

        private static void AddAttachmentsFromMailVm(BodyBuilder bodyBuilder, IList<IFormFile>? files)
        {
            if (files == null || files.Count == 0) return;

            foreach (var file in files)
            {
                if (file == null || file.Length <= 0) continue;

                using var ms = new MemoryStream();
                file.CopyTo(ms);

                // ✅ stream dispose olsa bile bytes ile eklediğimiz için problem yok
                bodyBuilder.Attachments.Add(file.FileName, ms.ToArray());
            }
        }

        private static void FillFilesFromBase64IfAny(MessageMQMail mail, ref int attCount, ref long attBytes)
        {
            if (mail.fileByteArrays == null || mail.fileByteArrays.Count == 0) return;

            mail.MailVM.Files = new List<IFormFile>();

            for (int i = 0; i < mail.fileByteArrays.Count; i++)
            {
                var base64File = mail.fileByteArrays[i];
                var fileFullName = mail.fileFullName[i];

                var fileBytes = Convert.FromBase64String(base64File);
                attCount++;
                attBytes += fileBytes.Length;

                var stream = new MemoryStream(fileBytes);

                var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName)
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/octet-stream"
                };

                mail.MailVM.Files.Add(file);
            }
        }

        private static void FillFilesFromBase64IfAny(MessageMQMailMultiple mail, ref int attCount, ref long attBytes)
        {
            if (mail.fileByteArrays == null || mail.fileByteArrays.Count == 0) return;

            // orijinal düzenine sadık: MailVM üstünden Files üretiliyor
            if (mail.MailVM == null) mail.MailVM = new MailVM();
            mail.MailVM.Files = new List<IFormFile>();

            for (int i = 0; i < mail.fileByteArrays.Count; i++)
            {
                var base64File = mail.fileByteArrays[i];
                var fileFullName = mail.fileFullName[i];

                var fileBytes = Convert.FromBase64String(base64File);
                attCount++;
                attBytes += fileBytes.Length;

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
            var smtp = Safe(_mail?.ExternalSmpt, 120);
            var port = _mail?.ExternalPort ?? 0;
            var ssl = _mail?.ExternalSsl ?? false;

            int attCount = 0;
            long attBytes = 0;

            try
            {
                var mimeMessage = new MimeMessage();
                mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));
                mimeMessage.To.Add(MailboxAddress.Parse($"{_mail.MailVM.BoxName} <{_mail.MailVM.To}>"));
                mimeMessage.Subject = _mail.MailVM.Subject;

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = _mail.MailVM.Body
                };

                // Base64 attachment listesi varsa Files'a dönüştür
                FillFilesFromBase64IfAny(_mail, ref attCount, ref attBytes);

                // Files üzerinden ekle
                AddAttachmentsFromMailVm(bodyBuilder, _mail.MailVM.Files);

                mimeMessage.Body = bodyBuilder.ToMessageBody();

                using var smtpClient = new SmtpClient();
                smtpClient.CheckCertificateRevocation = true;

                var host = _mail.ExternalSmpt;
                var options = ResolveSocketOptions(host, _mail.ExternalPort, _mail.ExternalSsl);

                await smtpClient.ConnectAsync(host, _mail.ExternalPort, options);

                // Gmail ise burada normal şifre değil App Password olmalı (ya da Workspace'te OAuth2)
                await smtpClient.AuthenticateAsync(_mail.ExternalMailAddress, _mail.ExternalConnectionKey);

                await smtpClient.SendAsync(mimeMessage);
                await smtpClient.DisconnectAsync(true);

                sw.Stop();

                Console.WriteLine(
                    $"[Success] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"to={to} sub=\"{subject}\" smtp={smtp} port={port} ssl={ssl} att={attCount} bytes={attBytes}"
                );

                sayac++;
            }
            catch (Exception ex)
            {
                sw.Stop();

                var errType = ex.GetType().Name;
                var errMsg = Safe(ex.Message, 400);

                Console.WriteLine(
                    $"[Fail] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"to={to} sub=\"{subject}\" smtp={smtp} port={port} ssl={ssl} att={attCount} bytes={attBytes} " +
                    $"errType={errType} err=\"{errMsg}\""
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
            var smtp = Safe(_mail?.ExternalSmpt, 120);
            var port = _mail?.ExternalPort ?? 0;
            var ssl = _mail?.ExternalSsl ?? false;

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

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = _mail.MailMultiVM.Body
                };

                // Base64 attachment listesi varsa MailVM.Files'a dönüştür
                FillFilesFromBase64IfAny(_mail, ref attCount, ref attBytes);

                // Multiple için: sende zaten _mail.MailMultiVM.Files var, orayı bozmayalım
                // Ama base64'tan üretilen dosyaları da eklemek istiyorsan:
                AddAttachmentsFromMailVm(bodyBuilder, _mail.MailVM?.Files);

                // Orijinal davranış: MailMultiVM.Files varsa onları ekle
                if (_mail.MailMultiVM.Files != null && _mail.MailMultiVM.Files.Count > 0)
                {
                    AddAttachmentsFromMailVm(bodyBuilder, _mail.MailMultiVM.Files);
                }

                mimeMessage.Body = bodyBuilder.ToMessageBody();

                using var smtpClient = new SmtpClient();
                smtpClient.CheckCertificateRevocation = true;

                var host = _mail.ExternalSmpt;
                var options = ResolveSocketOptions(host, _mail.ExternalPort, _mail.ExternalSsl);

                await smtpClient.ConnectAsync(host, _mail.ExternalPort, options);

                // Gmail ise burada normal şifre değil App Password olmalı (ya da Workspace'te OAuth2)
                await smtpClient.AuthenticateAsync(_mail.ExternalMailAddress, _mail.ExternalConnectionKey);

                await smtpClient.SendAsync(mimeMessage);
                await smtpClient.DisconnectAsync(true);

                sw.Stop();

                Console.WriteLine(
                    $"[Success] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"toCount={toCount} to={toList} sub=\"{subject}\" smtp={smtp} port={port} ssl={ssl} att={attCount} bytes={attBytes}"
                );

                sayac++;
            }
            catch (Exception ex)
            {
                sw.Stop();

                var errType = ex.GetType().Name;
                var errMsg = Safe(ex.Message, 400);

                Console.WriteLine(
                    $"[Fail] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"toCount={toCount} to={toList} sub=\"{subject}\" smtp={smtp} port={port} ssl={ssl} att={attCount} bytes={attBytes} " +
                    $"errType={errType} err=\"{errMsg}\""
                );

                throw;
            }
        }
    }
}
