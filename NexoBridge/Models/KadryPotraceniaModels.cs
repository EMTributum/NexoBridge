using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexoBridge.Models
{
    // Modele synchronicznych odczytów potrąceń komorniczych z baz Gratyfikanta - patrz KadryPotraceniaSqlService.
    // Koperty: camelCase. Pola samego potrącenia (Entry) mają nazwy snake_case (jak w JSON-ie z NexoBridgeKonsola)
    // i są słownikiem, bo zestaw pól jest duży i płaski. Daty jako "RRRR-MM-DD", kwoty jako liczby.
    // Lista (/lista) NIE zawiera PESEL, adresów ani kwot brutto; pełne dane tylko w /szczegoly.

    /// <summary>Jedna baza klienta z żądania listy.</summary>
    public class KadryClientDatabase
    {
        [JsonPropertyName("nip")]
        public string Nip { get; set; }

        [JsonPropertyName("databaseName")]
        public string DatabaseName { get; set; }
    }

    /// <summary>POST /api/kadry/potracenia/lista</summary>
    public class KadryPotraceniaListRequest
    {
        [JsonPropertyName("periodYear")]
        public int PeriodYear { get; set; }

        [JsonPropertyName("periodMonth")]
        public int PeriodMonth { get; set; }

        [JsonPropertyName("clientDatabases")]
        public List<KadryClientDatabase> ClientDatabases { get; set; }
    }

    public static class KadryPotraceniaStatus
    {
        public const string Ok = "OK";
        public const string NoDeductions = "NO_DEDUCTIONS";
        public const string Failed = "FAILED";
    }

    public class KadryPotraceniaListItem
    {
        [JsonPropertyName("nip")]
        public string Nip { get; set; }

        [JsonPropertyName("databaseName")]
        public string DatabaseName { get; set; }

        /// <summary>OK | NO_DEDUCTIONS | FAILED</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; }

        /// <summary>Tylko dla FAILED.</summary>
        [JsonPropertyName("error")]
        public string Error { get; set; }

        /// <summary>Ostrzeżenia danych (np. brak własnej firmy, potrącenie bez pracownika) - bez danych osobowych.</summary>
        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new();

        [JsonPropertyName("entries")]
        public List<Dictionary<string, object>> Entries { get; set; } = new();
    }

    public class KadryPotraceniaListResponse
    {
        [JsonPropertyName("periodYear")]
        public int PeriodYear { get; set; }

        [JsonPropertyName("periodMonth")]
        public int PeriodMonth { get; set; }

        [JsonPropertyName("items")]
        public List<KadryPotraceniaListItem> Items { get; set; } = new();
    }

    /// <summary>POST /api/kadry/potracenia/szczegoly</summary>
    public class KadryPotraceniaDetailsRequest
    {
        [JsonPropertyName("databaseName")]
        public string DatabaseName { get; set; }

        [JsonPropertyName("potracenieId")]
        public int PotracenieId { get; set; }

        [JsonPropertyName("periodYear")]
        public int PeriodYear { get; set; }

        [JsonPropertyName("periodMonth")]
        public int PeriodMonth { get; set; }
    }

    public class KadryPotraceniaDetailsResponse
    {
        [JsonPropertyName("entry")]
        public Dictionary<string, object> Entry { get; set; }
    }
}
