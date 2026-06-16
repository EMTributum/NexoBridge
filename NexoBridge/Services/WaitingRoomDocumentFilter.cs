using InsERT.Moria.ModelDanych;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NexoBridge.Services
{
    public sealed class WaitingRoomDocumentSelection
    {
        public List<DokumentDoKsiegowania> Included { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> OutsidePeriod { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> MissingDate { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> PartialAmortization { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> EmployeeBillsWithSubject { get; } = new List<DokumentDoKsiegowania>();

        public int Total =>
            Included.Count +
            OutsidePeriod.Count +
            MissingDate.Count +
            PartialAmortization.Count +
            EmployeeBillsWithSubject.Count;
    }

    public static class WaitingRoomDocumentFilter
    {
        private static readonly string[] EmployeeBillPropertyNames =
        {
            "RachunekDoUmowyPracowniczej",
            "SkladkiZUSRachunkuDoUmowyPracowniczej",
            "ZaliczkaRachunkuDoUmowyPracowniczej"
        };

        public static WaitingRoomDocumentSelection SelectForPeriod(IEnumerable<DokumentDoKsiegowania> documents, DateTime dataRozliczenia)
        {
            var selection = new WaitingRoomDocumentSelection();
            DateTime periodStart = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
            DateTime nextPeriodStart = periodStart.AddMonths(1);

            foreach (var document in documents ?? Enumerable.Empty<DokumentDoKsiegowania>())
            {
                if (document == null) continue;

                if (CzyCzastkowaAmortyzacja(document))
                {
                    selection.PartialAmortization.Add(document);
                    continue;
                }

                DateTime? documentDate = PobierzDateDokumentu(document);
                if (!documentDate.HasValue)
                {
                    selection.MissingDate.Add(document);
                    continue;
                }

                DateTime date = documentDate.Value.Date;
                if (date < periodStart || date >= nextPeriodStart)
                {
                    selection.OutsidePeriod.Add(document);
                    continue;
                }

                if (CzyRachunekDoUmowyPracowniczejZPodmiotem(document))
                {
                    selection.EmployeeBillsWithSubject.Add(document);
                    continue;
                }

                selection.Included.Add(document);
            }

            return selection;
        }

        public static bool CzyCzastkowaAmortyzacja(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return false;

            try
            {
                if (PobierzWlasciwosc(dokument, "OperacjaAMZ") != null) return false;
            }
            catch { }

            try
            {
                var operacja = PobierzWlasciwosc(dokument, "OperacjaST");
                if (operacja == null) return false;

                return operacja is OperacjaAM || operacja.GetType().Name.Contains("OperacjaAM");
            }
            catch
            {
                return false;
            }
        }

        public static DateTime? PobierzDateDokumentu(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return null;

            DateTime? data = PobierzDateWlasciwosci(dokument, "Data");
            if (data.HasValue) return data.Value.Date;

            data = PobierzDateWlasciwosci(dokument, "DataWystawienia");
            if (data.HasValue) return data.Value.Date;

            foreach (var rachunek in PobierzRachunkiPracownicze(dokument))
            {
                data = PobierzDateWlasciwosci(rachunek, "Data");
                if (data.HasValue) return data.Value.Date;

                data = PobierzDateWlasciwosci(rachunek, "DataWystawienia");
                if (data.HasValue) return data.Value.Date;
            }

            var operacjaAmz = PobierzWlasciwosc(dokument, "OperacjaAMZ");
            data = PobierzDateWlasciwosci(operacjaAmz, "Data");
            if (data.HasValue) return data.Value.Date;

            var operacjaSt = PobierzWlasciwosc(dokument, "OperacjaST");
            data = PobierzDateWlasciwosci(operacjaSt, "Data");
            if (data.HasValue) return data.Value.Date;

            return null;
        }

        public static bool CzyRachunekDoUmowyPracowniczej(DokumentDoKsiegowania dokument)
        {
            return PobierzRachunkiPracownicze(dokument).Any();
        }

        public static bool CzyRachunekDoUmowyPracowniczejZPodmiotem(DokumentDoKsiegowania dokument)
        {
            var rachunki = PobierzRachunkiPracownicze(dokument).ToList();
            if (rachunki.Count == 0) return false;

            if (MaPodmiot(dokument)) return true;
            return rachunki.Any(MaPodmiot);
        }

        private static IEnumerable<object> PobierzRachunkiPracownicze(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) yield break;

            foreach (string propertyName in EmployeeBillPropertyNames)
            {
                var value = PobierzWlasciwosc(dokument, propertyName);
                if (value != null) yield return value;
            }
        }

        private static bool MaPodmiot(object source)
        {
            if (source == null) return false;

            return PobierzWlasciwosc(source, "Podmiot") != null ||
                   PobierzWlasciwosc(source, "PodmiotHistoria") != null;
        }

        private static DateTime? PobierzDateWlasciwosci(object source, string propertyName)
        {
            object value = PobierzWlasciwosc(source, propertyName);
            if (value == null) return null;

            if (value is DateTime dateTime) return dateTime;
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.DateTime;
            if (DateTime.TryParse(value.ToString(), out DateTime parsed)) return parsed;

            return null;
        }

        private static object PobierzWlasciwosc(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            try
            {
                var property = source.GetType().GetProperty(propertyName);
                return property?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }
    }
}
