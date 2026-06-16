using InsERT.Moria.ModelDanych;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

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

            miesiacRozliczeniowy = PobierzDataOtrzymaniaZEpp(dokument);
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

        private static DateTime? PobierzDataOtrzymaniaZEpp(DokumentDoKsiegowania dokument)
        {
            object xmlObj = PobierzWlasciwosc(dokument, "DokumentDoKsiegowaniaXML");
            if (xmlObj == null) return null;

            string xml = PobierzWlasciwosc(xmlObj, "XML") as string;
            if (string.IsNullOrWhiteSpace(xml))
            {
                xml = RozpakujXmlDokumentu(PobierzWlasciwosc(xmlObj, "XMLSkompresowany") as byte[]);
            }

            if (string.IsNullOrWhiteSpace(xml)) return null;

            string epp = WyciagnijEppZXml(xml);
            if (string.IsNullOrWhiteSpace(epp)) return null;

            return PobierzDateOtrzymaniaZNaglowkaEpp(epp);
        }

        private static string RozpakujXmlDokumentu(byte[] compressed)
        {
            if (compressed == null || compressed.Length == 0) return null;

            try
            {
                using (var input = new MemoryStream(compressed))
                using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
                using (var reader = new StreamReader(zlib, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string WyciagnijEppZXml(string xml)
        {
            try
            {
                return XDocument.Parse(xml).Root?.Element("Dokument")?.Value;
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? PobierzDateOtrzymaniaZNaglowkaEpp(string epp)
        {
            using (var reader = new StringReader(epp))
            {
                bool naglowek = false;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;

                    if (trimmed.Equals("[NAGLOWEK]", StringComparison.OrdinalIgnoreCase))
                    {
                        naglowek = true;
                        continue;
                    }

                    if (!naglowek) continue;
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        naglowek = false;
                        continue;
                    }

                    var fields = PodzielLinieCsvEpp(trimmed);
                    naglowek = false;

                    if (fields.Count <= 23 || !CzyNaglowekLogistyki(fields[0])) continue;

                    DateTime? dataOtrzymania = ParsujDateEpp(fields[23]);
                    if (dataOtrzymania.HasValue) return dataOtrzymania.Value.Date;
                }
            }

            return null;
        }

        private static bool CzyNaglowekLogistyki(string typ)
        {
            if (string.IsNullOrWhiteSpace(typ)) return false;

            string normalized = typ.Trim().ToUpperInvariant();
            return normalized == "FZ" ||
                   normalized == "FS" ||
                   normalized == "FZK" ||
                   normalized == "FSK" ||
                   normalized == "PA";
        }

        private static DateTime? ParsujDateEpp(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8) return null;

            string date = raw.Trim().Substring(0, 8);
            if (DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
            {
                return parsed;
            }

            return null;
        }

        private static List<string> PodzielLinieCsvEpp(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                    continue;
                }

                if (c == ',' && !inQuotes)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            fields.Add(current.ToString());
            return fields;
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
