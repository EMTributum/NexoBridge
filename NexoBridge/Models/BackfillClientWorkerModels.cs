using System.Collections.Generic;

namespace NexoBridge.Models
{
    /// <summary>
    /// Kontrakt JSON między procesem głównym NexoBridge a procesem potomnym uruchamianym jako
    /// "NexoBridge.exe --backfill-enumerate-client-worker" (patrz Program.cs) - liczy/enumeruje
    /// dokumenty DOKŁADNIE JEDNEGO klienta w izolowanym procesie systemowym. Ten sam powód co
    /// PayrollClientWorkerRequest/PayrollCountsService.PoliczDlaKlientaWProcesie: powtarzane
    /// logowania Sfery w JEDNYM procesie psują jej stan statyczny nawet przy pełnej serializacji
    /// (SferaSessionGate) - jedyny solidny fix to świeży proces per klient.
    /// </summary>
    public class BackfillEnumerateClientWorkerRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string ClientNip { get; set; }
        public string ClientName { get; set; }
        public string DatabaseName { get; set; }
        public int Year { get; set; }
    }

    public class BackfillEnumerateClientWorkerResponse
    {
        public string Status { get; set; } // SUCCESS / FAILED
        public string Error { get; set; }
        public List<BackfillManifestRow> Rows { get; set; } = new List<BackfillManifestRow>();
        public int SkippedAlreadyLinked { get; set; }
    }

    /// <summary>Analogiczny kontrakt dla "--backfill-write-comments-worker" - dopisuje komentarze z
    /// linkiem dla WSZYSTKICH wierszy jednej bazy (DatabaseName) w jednym, izolowanym procesie.</summary>
    public class BackfillWriteCommentsClientWorkerRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public List<BackfillCommentRow> Rows { get; set; } = new List<BackfillCommentRow>();
    }

    public class BackfillWriteCommentsClientWorkerResponse
    {
        public string Status { get; set; } // SUCCESS / FAILED
        public string Error { get; set; }
        public int Written { get; set; }
        public int SkippedAlreadyLinked { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }
}
