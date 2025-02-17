using System;
using System.Threading.Tasks;
using MailConsumerRabbitMQ.Modals;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Mail Consumer başlatılıyor...");

        var mailConsumer = new MailConsumer();
        await mailConsumer.InitializeAsync(); // Bağlantıyı başlat
        mailConsumer.StartListening(); // Mesajları dinlemeye başla

        Console.WriteLine("Mail Consumer çalışıyor. Çıkmak için Ctrl + C bas.");
        await Task.Delay(-1); // Programın sürekli çalışmasını sağla
    }
}
