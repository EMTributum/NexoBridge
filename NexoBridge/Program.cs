using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System;
using DotNetEnv;

namespace NexoBridge
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // 1. Wczytanie bezpiecznych haseł z pliku .env od razu na starcie
            Env.Load();

            // 2. Inicjalizacja kreatora serwera Web API
            var builder = WebApplication.CreateBuilder(args);

            // Tutaj w przyszłości będziemy rejestrować Sferę i kolejkę zadań (tzw. Wstrzykiwanie Zależności)
            // builder.Services.AddSingleton<...>();

            var app = builder.Build();

            // 3. Konfiguracja naszych końcówek (Endpointów) REST

            // Prosty endpoint testowy (Health Check)
            app.MapGet("/ping", () => Results.Ok(new { Status = "Online", Message = "NexoBridge nasłuchuje na żądania!" }));

            Console.WriteLine("Uruchamianie mikroserwisu NexoBridge...");

            // 4. Start serwera na sztywno przypisanym porcie (np. 5000)
            app.Run("http://localhost:5000");
        }
    }
}