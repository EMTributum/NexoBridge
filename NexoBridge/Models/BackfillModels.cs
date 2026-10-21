using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class BackfillClientRef
    {
        public string Nip { get; set; }
        public string DatabaseName { get; set; }
        public string ClientName { get; set; }
        // "Rachmistrz" (domyślnie) albo "Rewizor" - produkt sesji Sfery dla bazy klienta. Przy złym produkcie
        // logowanie się nie udaje, a NexoBridge ponawia z drugim (BackfillService.EnumerateAsync).
        public string Product { get; set; }
    }

    public class BackfillEnumerateJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int Year { get; set; }
        // Tryb weryfikacji/naprawy w Klasyfikatorze: zapisy, które już mają link, NIE są pomijane, tylko
        // wracają z ExistingLinks - Klasyfikator porównuje je z dopasowaniem w Scanye.
        public bool IncludeLinked { get; set; }
        public List<BackfillClientRef> Clients { get; set; } = new List<BackfillClientRef>();
    }

    public class BackfillManifestRow
    {
        public string ClientNip { get; set; }
        public string DatabaseName { get; set; }
        public string EntityType { get; set; }
        public int RachmistrzId { get; set; }
        public string VendorNip { get; set; }
        public string NumerDokumentu { get; set; }
        // Warianty numeru do wyszukania w Scanye, od najbardziej prawdopodobnego (Klasyfikator sprawdza je
        // po kolei). Numer bywa zapisany jako czysty numer na zapisie albo jako etykieta "FZ 2 16446"
        // na dokumencie do księgowania - zależy od tego, jak dokument trafił do Rachmistrza.
        public List<string> NumerKandydaci { get; set; } = new List<string>();
        public int? RecordYear { get; set; }
        // Produkt, którym faktycznie udało się zalogować przy enumeracji - Klasyfikator odsyła go w BackfillCommentRow.
        public string Product { get; set; }
        // Tylko przy IncludeLinked: komentarze z linkiem do podglądu, które zapis już ma.
        public List<BackfillExistingLink> ExistingLinks { get; set; } = new List<BackfillExistingLink>();
    }

    public class BackfillExistingLink
    {
        public int CommentId { get; set; }
        public string ViewerUrl { get; set; }
        // Komentarz zawiera wyłącznie nasz link - tylko taki wolno podmienić przy naprawie.
        public bool PureLink { get; set; }
    }

    public class BackfillEnumerateReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } = "PENDING";
        public string Message { get; set; }
        public List<BackfillManifestRow> Rows { get; set; } = new List<BackfillManifestRow>();
        public List<string> ClientErrors { get; set; } = new List<string>();
        public int ClientsOk { get; set; }
        public int ClientsFailed { get; set; }
        // Zapisy pominięte przy enumeracji, bo mają już komentarz z linkiem do podglądu.
        public int SkippedAlreadyLinked { get; set; }
        // Potwierdzenie dla Klasyfikatora, że ta wersja NexoBridge obsłużyła IncludeLinked (starsza zwracałaby
        // wiersze bez ExistingLinks, a naprawa dopisałaby wtedy drugi link zamiast podmienić zły).
        public bool LinkedIncluded { get; set; }
    }

    public class BackfillCommentRow
    {
        public string DatabaseName { get; set; }
        public string EntityType { get; set; }
        public int RachmistrzId { get; set; }
        public string ViewerUrl { get; set; }
        public string NumerDokumentu { get; set; }
        public string Product { get; set; }
        // Naprawa: komentarze z błędnym linkiem na tym zapisie, w których podmieniamy URL na ViewerUrl
        // (zamiast dopisywać nowy komentarz).
        public List<int> ReplaceCommentIds { get; set; } = new List<int>();
    }

    public class BackfillWriteCommentsJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public List<BackfillCommentRow> Rows { get; set; } = new List<BackfillCommentRow>();
    }

    public class BackfillWriteCommentsReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } = "PENDING";
        public string Message { get; set; }
        public int Written { get; set; }
        public int Replaced { get; set; }
        public int SkippedAlreadyLinked { get; set; }
        public int Failed { get; set; }
        // Odczyt kontrolny po zapisie (projekcja z bazy): komentarz istnieje, wisi na właściwym zapisie, ma link.
        public int Verified { get; set; }
        public int Unverified { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }
}
