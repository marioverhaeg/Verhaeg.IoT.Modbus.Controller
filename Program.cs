namespace Verhaeg.IoT.Modbus.Controller
{
    public class Program
    {
        public static void Main(string[] args)
        {
            IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureServices(services =>
                {
                    services.AddHostedService<Worker>();
                })
                .UseSystemd()
                .Build();

            host.Run();
        }
    }
}