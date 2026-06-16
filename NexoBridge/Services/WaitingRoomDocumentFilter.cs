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
        public List<DokumentDoKsiegowania> MissingAccountingPeriod { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> PartialAmortization { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> EmployeeBillsWithSubject { get; } = new List<DokumentDoKsiegowania>();

        public int Total =>
            Included.Count +
            OutsidePeriod.Count +
            MissingAccountingPeriod.Count +
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

            foreach (var document in documents ?? Enumerable.Empty<DokumentDoKsiegowania>())
            {
                if (document == null) continue;

                if (CzyCzastkowaAmortyzacja(document))
                {
                    selection.PartialAmortization.Add(document);
                    continue;
                }

                DateTime? accountingPeriod = PobierzMiesiacKsiegowyDokumentu(document);
                if (!accountingPeriod.HasValue)
                {
                    selection.MissingAccountingPeriod.Add(document);
                    continue;
                }

                DateTime period = new DateTime(accountingPeriod.Value.Year, accountingPeriod.Value.Month, 1);
                if (period != periodStart)
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

        public static DateTime? PobierzMiesiacKsiegowyDokumentu(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return null;

            DateTime? miesiacRozliczeniowy = PobierzDateSciezki(
                dokument,
                "PortalBiura2DokumentOdKlienta.OpisDokumentu.MiesiacRozliczeniowy");
            if (miesiacRozliczeniowy.HasValue) return miesiacRozliczeniowy.Value.Date;

            miesiacRozliczeniowy = PobierzDateSciezki(
                dokument,
                "PortalFirmyDokumentZPortaluFirmy.OpisDokumentu.MiesiacRozliczeniowy");
            if (miesiacRozliczeniowy.HasValue) return miesiacRozliczeniowy.Value.Date;

            miesiacRozliczeniowy = PobierzDateSciezki(
                dokument,
                "DokumentElektroniczny.OpisDokumentu.MiesiacRozliczeniowy");
            if (miesiacRozliczeniowy.HasValue) return miesiacRozliczeniowy.Value.Date;

            return PobierzDateDokumentuTechnicznego(dokument);
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

        private static DateTime? PobierzDateDokumentuTechnicznego(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return null;

            DateTime? data;
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

        private static DateTime? PobierzDateSciezki(object source, string path)
        {
            object current = source;
            foreach (string propertyName in path.Split('.'))
            {
                current = PobierzWlasciwosc(current, propertyName);
                if (current == null) return null;
            }

            if (current is DateTime dateTime) return dateTime;
            if (current is DateTimeOffset dateTimeOffset) return dateTimeOffset.DateTime;
            if (DateTime.TryParse(current.ToString(), out DateTime parsed)) return parsed;

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
