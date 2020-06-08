using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TorrentSpider
{
    public class Program
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="args"></param>
        public static void Main (string[] args)
        {
            CreateHostBuilder (args).Build ().Run ();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public static IHostBuilder CreateHostBuilder (string[] args)
        {
            var conf = new ConfigurationBuilder ()
                .SetBasePath (Directory.GetCurrentDirectory ())
                .AddJsonFile ("appsettings.json", true, true)
                .Build ();

            return Host.CreateDefaultBuilder (args)
                .ConfigureServices ((hostContext, services) => {
                    services.Configure<Appsettings> (conf);
                    services.AddHostedService<Worker> ();
                });
        }
    }
}
