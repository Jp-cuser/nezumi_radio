using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NezumiRadio
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();
            var builder = Host.CreateApplicationBuilder(args);
            
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<AudiusApiService>();
            builder.Services.AddSingleton<RadioSystem>();
            builder.Services.AddHostedService(x => x.GetRequiredService<RadioSystem>());
            
            var host = builder.Build();
            await host.RunAsync();
        }
    }
}
