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
        private  ConnectionFactory _factory;
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
                        string base64File = _mail.fileByteArrays[index]; // Base64 kodlu dosya
                        string fileFullName = _mail.fileFullName[index]; // Dosya adı (tam ad ve uzantı)

                        byte[] fileBytes = Convert.FromBase64String(base64File); // Base64'ü byte dizisine çevir
                        var stream = new MemoryStream(fileBytes); // Byte dizisini Stream'e çevir

                        var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName) // Özel isim verebilirsin
                        {
                            Headers = new HeaderDictionary(),
                            ContentType = "application/octet-stream"
                        };

                        _mail.MailVM.Files.Add(file); // Yeni IFormFile nesnesini listeye ekle
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

                Console.WriteLine(sayac+")"+_mail.MailVM.Subject+" Mail başarıyla gönderildi."+ DateTime.Now.ToString("dd-MMM HH:m:s") + " "+_mail.MailVM.To);
                sayac++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
                throw;
            }
        }
        private async Task SendEmailMultiple(MessageMQMailMultiple _mail)
        {
            try
            {
                MimeMessage mimeMessage = new MimeMessage();
                mimeMessage.From.Add(MailboxAddress.Parse($"{_mail.ExternalMailAddress} <{_mail.ExternalMailAddress}>"));
              
                foreach (var item in _mail.MailMultiVM.ToMultipleBoxAdress)
                    mimeMessage.To.Add(MailboxAddress.Parse($"{item.Item1} <{item.Item2}>"));
                mimeMessage.Subject = _mail.MailMultiVM.Subject;
                var bodyBuilder = new BodyBuilder();
                bodyBuilder.HtmlBody = _mail.MailMultiVM.Body;

                List<MailboxAddress> toAddresses = new List<MailboxAddress>();
                string sendedTo = "";
                foreach ((string item,string item2) in _mail.MailMultiVM.ToMultipleBoxAdress)
                {
                    MailboxAddress recipient = new MailboxAddress(item, item2);
                    sendedTo+=item2+",";
                    mimeMessage.To.Add(recipient);
                }

                if (_mail.fileByteArrays != null && _mail.fileByteArrays.Count > 0)
                {
                    _mail.MailVM.Files = new List<IFormFile>();

                    for (int index = 0; index < _mail.fileByteArrays.Count; index++)
                    {
                        string base64File = _mail.fileByteArrays[index]; // Base64 kodlu dosya
                        string fileFullName = _mail.fileFullName[index]; // Dosya adı (tam ad ve uzantı)

                        byte[] fileBytes = Convert.FromBase64String(base64File); // Base64'ü byte dizisine çevir
                        var stream = new MemoryStream(fileBytes); // Byte dizisini Stream'e çevir

                        var file = new FormFile(stream, 0, fileBytes.Length, "file", fileFullName) // Özel isim verebilirsin
                        {
                            Headers = new HeaderDictionary(),
                            ContentType = "application/octet-stream"
                        };

                        _mail.MailVM.Files.Add(file); // Yeni IFormFile nesnesini listeye ekle
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

                Console.WriteLine(sayac+")"+_mail.MailMultiVM.Subject+" Mail başarıyla gönderildi."+ DateTime.Now.ToString("dd-MMM HH:m:s") + "=> "+sendedTo);




            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
                throw;
            }
        }
    }
}
