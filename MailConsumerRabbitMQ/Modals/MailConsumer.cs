using System;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using MimeKit;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;

namespace MailConsumerRabbitMQ.Modals
{
    public class MailConsumer
    {
        private ConnectionFactory _factory;
        private IConnection _connection;
        private IChannel _channel;

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

            // RabbitMQ bağlantısı oluştur (senkron API kullanıldığı için doğrudan çağırılıyor)
            _connection =await _factory.CreateConnectionAsync();
            _channel =await _connection.CreateChannelAsync();

            // Kuyruğu tanımla (Asenkron olmayan metot olduğu için direkt çağırılıyor)
            await _channel.QueueDeclareAsync(queue: "mail_queue",
                                   durable: true,
                                   exclusive: false,
                                   autoDelete: false,
                                   arguments: null);

            await Task.CompletedTask; // Metodun async yapısını korumak için ekledik
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

                    if (mailProperties != null&&mailProperties.MailVM!=null)
                    {
                        await SendEmail(mailProperties);
                        await _channel.BasicAckAsync(ea.DeliveryTag, false); // Mesaj işlendiyse onayla
                    }
                    else
                    {
                        var mailPropertiess = JsonConvert.DeserializeObject<MessageMQMailMultiple>(message);
                        await SendEmailMultiple(mailPropertiess);
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
                    await _channel.BasicAckAsync(ea.DeliveryTag, false); // Hata olursa mesajı tekrar kuyruğa koy
                }
            };

            _channel.BasicConsumeAsync(queue: "mail_queue",
                                  autoAck: false, // Manuel onaylama
                                  consumer: consumer);

            Console.WriteLine("MailConsumer çalışıyor, mail kuyruğu dinleniyor...");
        }
        public int sayac = 1;


        private async Task SendEmail(MessageMQMail _mail)
        {
            // ---- LOG helper: tek satır, grep-friendly ----
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

            // Bu değerleri log için en baştan hazırlayalım (null riskine karşı)
            var to = Safe(_mail?.MailVM?.To, 200);
            var subject = Safe(_mail?.MailVM?.Subject, 200);
            var smtp = Safe(_mail?.ExternalSmpt, 120);
            var port = _mail?.ExternalPort ?? 0;
            var ssl = _mail?.ExternalSsl ?? false;

            int attCount = 0;
            long attBytes = 0;

            try
            {
                MimeMessage mimeMessage = new MimeMessage();
                mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));
                mimeMessage.To.Add(MailboxAddress.Parse($"{_mail.MailVM.BoxName} <{_mail.MailVM.To}>"));
                mimeMessage.Subject = _mail.MailVM.Subject;

                var bodyBuilder = new BodyBuilder();
                bodyBuilder.HtmlBody = _mail.MailVM.Body;

                if (_mail.fileByteArrays != null && _mail.fileByteArrays.Count > 0)
                {
                    _mail.MailVM.Files = new List<IFormFile>();

                    for (int index = 0; index < _mail.fileByteArrays.Count; index++)
                    {
                        string base64File = _mail.fileByteArrays[index];
                        string fileFullName = _mail.fileFullName[index];

                        byte[] fileBytes = Convert.FromBase64String(base64File);
                        attCount++;
                        attBytes += fileBytes?.Length ?? 0;

                        var stream = new MemoryStream(fileBytes);

                        var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName)
                        {
                            Headers = new HeaderDictionary(),
                            ContentType = "application/octet-stream"
                        };

                        _mail.MailVM.Files.Add(file);
                    }
                }

                if (_mail.MailVM.Files != null && _mail.MailVM.Files.Count > 0)
                {
                    foreach (var file in _mail.MailVM.Files)
                    {
                        using var memoryStream = new MemoryStream();
                        file.CopyTo(memoryStream);
                        memoryStream.Seek(0, SeekOrigin.Begin);
                        bodyBuilder.Attachments.Add(file.FileName, memoryStream);
                    }
                }

                mimeMessage.Body = bodyBuilder.ToMessageBody();

                using var smtpClient = new SmtpClient();
                await smtpClient.ConnectAsync(_mail.ExternalSmpt, _mail.ExternalPort, _mail.ExternalSsl);
                await smtpClient.AuthenticateAsync(_mail.ExternalMailAddress, _mail.ExternalConnectionKey);
                await smtpClient.SendAsync(mimeMessage);
                await smtpClient.DisconnectAsync(true);

                sw.Stop();

                // ---- SUCCESS LOG (tek satır) ----
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

                // ---- FAIL LOG (tek satır) ----
                Console.WriteLine(
                    $"[Fail] ts={started:yyyy-MM-dd HH:mm:ss.fff} no={sayac} durMs={sw.ElapsedMilliseconds} " +
                    $"to={to} sub=\"{subject}\" smtp={smtp} port={port} ssl={ssl} att={attCount} bytes={attBytes} " +
                    $"errType={errType} err=\"{errMsg}\""
                );

                throw;
            }
        }

        private async Task SendEmailMultiple(MessageMQMailMultiple _mail)
        {
            // ---- LOG helper: tek satır, grep-friendly ----
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

            // log için ön bilgiler (null riskine karşı)
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
                // sadece mail adresleri, tek satır
                toList = string.Join(",", _mail.MailMultiVM.ToMultipleBoxAdress.Select(x => x.Item2));
                toList = Safe(toList, 260);
            }

            try
            {
                MimeMessage mimeMessage = new MimeMessage();
                mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));

                // ✅ ALICI EKLEME SADECE 1 KEZ (tekrar yok)
                foreach (var item in _mail.MailMultiVM.ToMultipleBoxAdress)
                    mimeMessage.To.Add(MailboxAddress.Parse($"{item.Item1} <{item.Item2}>"));

                mimeMessage.Subject = _mail.MailMultiVM.Subject;

                var bodyBuilder = new BodyBuilder();
                bodyBuilder.HtmlBody = _mail.MailMultiVM.Body;

                List<MailboxAddress> toAddresses = new List<MailboxAddress>(); // düzen bozulmasın diye bıraktım
                string sendedTo = "";

                // ✅ burada artık mimeMessage.To.Add YOK, sadece log için string üretiyor
                foreach ((string item, string item2) in _mail.MailMultiVM.ToMultipleBoxAdress)
                {
                    sendedTo += item2 + ",";
                }

                if (_mail.fileByteArrays != null && _mail.fileByteArrays.Count > 0)
                {
                    // orijinal düzenini bozmayalım: MailVM üstünden Files oluşturuyordun
                    if (_mail.MailVM == null) _mail.MailVM = new MailVM();
                    _mail.MailVM.Files = new List<IFormFile>();

                    for (int index = 0; index < _mail.fileByteArrays.Count; index++)
                    {
                        string base64File = _mail.fileByteArrays[index];
                        string fileFullName = _mail.fileFullName[index];

                        byte[] fileBytes = Convert.FromBase64String(base64File);
                        attCount++;
                        attBytes += fileBytes?.Length ?? 0;

                        var stream = new MemoryStream(fileBytes);

                        var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName)
                        {
                            Headers = new HeaderDictionary(),
                            ContentType = "application/octet-stream"
                        };

                        _mail.MailVM.Files.Add(file);
                    }
                }

                if (_mail.MailMultiVM.Files != null && _mail.MailMultiVM.Files.Count > 0)
                {
                    foreach (var file in _mail.MailMultiVM.Files)
                    {
                        using var memoryStream = new MemoryStream();
                        file.CopyTo(memoryStream);
                        memoryStream.Seek(0, SeekOrigin.Begin);
                        bodyBuilder.Attachments.Add(file.FileName, memoryStream);
                    }
                }

                mimeMessage.Body = bodyBuilder.ToMessageBody();

                using var smtpClient = new SmtpClient();
                await smtpClient.ConnectAsync(_mail.ExternalSmpt, _mail.ExternalPort, _mail.ExternalSsl);
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



        //private async Task SendEmail(MessageMQMail _mail)
        //{
        //    try
        //    {
        //        MimeMessage mimeMessage = new MimeMessage();
        //        mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));
        //        mimeMessage.To.Add(MailboxAddress.Parse($"{_mail.MailVM.BoxName} <{_mail.MailVM.To}>"));
        //        mimeMessage.Subject = _mail.MailVM.Subject;
        //        var bodyBuilder = new BodyBuilder();
        //        bodyBuilder.HtmlBody = _mail.MailVM.Body;

        //        if (_mail.fileByteArrays != null && _mail.fileByteArrays.Count > 0)
        //        {
        //            _mail.MailVM.Files = new List<IFormFile>();

        //            for (int index = 0; index < _mail.fileByteArrays.Count; index++)
        //            {
        //                string base64File = _mail.fileByteArrays[index]; // Base64 kodlu dosya
        //                string fileFullName = _mail.fileFullName[index]; // Dosya adı (tam ad ve uzantı)

        //                byte[] fileBytes = Convert.FromBase64String(base64File); // Base64'ü byte dizisine çevir
        //                var stream = new MemoryStream(fileBytes); // Byte dizisini Stream'e çevir

        //                var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName) // Özel isim verebilirsin
        //                {
        //                    Headers = new HeaderDictionary(),
        //                    ContentType = "application/octet-stream"
        //                };

        //                _mail.MailVM.Files.Add(file); // Yeni IFormFile nesnesini listeye ekle
        //            }

        //        }
        //        if (_mail.MailVM.Files != null && _mail.MailVM.Files.Count > 0)
        //        {
        //            foreach (var file in _mail.MailVM.Files)
        //            {
        //                using var memoryStream = new MemoryStream();
        //                file.CopyTo(memoryStream);
        //                memoryStream.Seek(0, SeekOrigin.Begin);
        //                bodyBuilder.Attachments.Add(file.FileName, memoryStream);
        //            }
        //        }
        //        mimeMessage.Body = bodyBuilder.ToMessageBody();
        //        using var smtpClient = new SmtpClient();
        //        await smtpClient.ConnectAsync(_mail.ExternalSmpt, _mail.ExternalPort, _mail.ExternalSsl);
        //        await smtpClient.AuthenticateAsync(_mail.ExternalMailAddress, _mail.ExternalConnectionKey);
        //        await smtpClient.SendAsync(mimeMessage);
        //        await smtpClient.DisconnectAsync(true);

        //        Console.WriteLine(sayac+")"+_mail.MailVM.Subject+" Mail başarıyla gönderildi."+ DateTime.Now.ToString("dd-MMM HH:m:s") + " "+_mail.MailVM.To);
        //        sayac++;
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
        //        throw;
        //    }
        //}
        //private async Task SendEmailMultiple(MessageMQMailMultiple _mail)
        //{
        //    try
        //    {
        //        MimeMessage mimeMessage = new MimeMessage();
        //        mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));

        //        foreach (var item in _mail.MailMultiVM.ToMultipleBoxAdress)
        //            mimeMessage.To.Add(MailboxAddress.Parse($"{item.Item1} <{item.Item2}>"));
        //        mimeMessage.Subject = _mail.MailMultiVM.Subject;
        //        var bodyBuilder = new BodyBuilder();
        //        bodyBuilder.HtmlBody = _mail.MailMultiVM.Body;

        //        List<MailboxAddress> toAddresses = new List<MailboxAddress>();
        //        string sendedTo = "";
        //        foreach ((string item,string item2) in _mail.MailMultiVM.ToMultipleBoxAdress)
        //        {
        //            MailboxAddress recipient = new MailboxAddress(item, item2);
        //            sendedTo+=item2+",";
        //            mimeMessage.To.Add(recipient);
        //        }

        //        if (_mail.fileByteArrays != null && _mail.fileByteArrays.Count > 0)
        //        {
        //            _mail.MailVM.Files = new List<IFormFile>();

        //            for (int index = 0; index < _mail.fileByteArrays.Count; index++)
        //            {
        //                string base64File = _mail.fileByteArrays[index]; // Base64 kodlu dosya
        //                string fileFullName = _mail.fileFullName[index]; // Dosya adı (tam ad ve uzantı)

        //                byte[] fileBytes = Convert.FromBase64String(base64File); // Base64'ü byte dizisine çevir
        //                var stream = new MemoryStream(fileBytes); // Byte dizisini Stream'e çevir

        //                var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName) // Özel isim verebilirsin
        //                {
        //                    Headers = new HeaderDictionary(),
        //                    ContentType = "application/octet-stream"
        //                };

        //                _mail.MailVM.Files.Add(file); // Yeni IFormFile nesnesini listeye ekle
        //            }

        //        }
        //        if (_mail.MailMultiVM.Files != null && _mail.MailMultiVM.Files.Count > 0)
        //        {
        //            foreach (var file in _mail.MailMultiVM.Files)
        //            {
        //                using var memoryStream = new MemoryStream();
        //                file.CopyTo(memoryStream);
        //                memoryStream.Seek(0, SeekOrigin.Begin);
        //                bodyBuilder.Attachments.Add(file.FileName, memoryStream);
        //            }
        //        }
        //        mimeMessage.Body = bodyBuilder.ToMessageBody();
        //        using var smtpClient = new SmtpClient();
        //        await smtpClient.ConnectAsync(_mail.ExternalSmpt, _mail.ExternalPort, _mail.ExternalSsl);
        //        await smtpClient.AuthenticateAsync(_mail.ExternalMailAddress, _mail.ExternalConnectionKey);
        //        await smtpClient.SendAsync(mimeMessage);
        //        await smtpClient.DisconnectAsync(true);

        //        Console.WriteLine(sayac+")"+_mail.MailMultiVM.Subject+" Mail başarıyla gönderildi."+ DateTime.Now.ToString("dd-MMM HH:m:s") + "=> "+sendedTo);




        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
        //        throw;
        //    }
        //}
    }
}
