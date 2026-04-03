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
            // Endpoint do przyjmowania paczek z fakturami
            app.MapPost("/api/jobs/import", async (HttpRequest request) =>
            {
                // 1. Sprawdzamy, czy ktoś w ogóle wysłał pliki/formularz
                if (!request.HasFormContentType)
                    return Results.BadRequest("Oczekiwano formularza multipart/form-data.");

                // 2. Odbieramy dane z formularza
                var form = await request.ReadFormAsync();
                var username = form["Username"].ToString();
                var password = form["Password"].ToString();
                var files = form.Files;

                // 3. Weryfikacja czy niczego nie brakuje
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                    return Results.BadRequest("Brak loginu lub hasła do Nexo.");

                if (files.Count == 0)
                    return Results.BadRequest("Nie przesłano żadnych plików EPP.");

                // 4. (Tymczasowo) Tylko wypisujemy w konsoli, co dostaliśmy
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n--- OTRZYMANO NOWE ZLECENIE IMPORTU ---");
                Console.WriteLine($"Użytkownik: {username}");
                Console.WriteLine($"Ilość plików EPP: {files.Count}");

                long totalSize = 0;
                foreach (var file in files)
                {
                    Console.WriteLine($" - {file.FileName} ({file.Length} bajtów)");
                    totalSize += file.Length;
                }
                Console.ResetColor();

                // 5. Zwracamy HTTP 202 (Zaakceptowano do przetwarzania) oraz sztuczne JobId
                return Results.Accepted(value: new
                {
                    JobId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Message = $"Pomyślnie odebrano {files.Count} plików EPP.",
                    TotalBytes = totalSize
                });
            });
            Console.WriteLine("Uruchamianie mikroserwisu NexoBridge...");

            // 4. Start serwera na sztywno przypisanym porcie (np. 5000)
            app.Run("http://localhost:5000");
        }
    }
}