using InsERT.Moria;
using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ModelDanych;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NexoBridge.Services
{
    public sealed class WaitingRoomDocumentSelection
    {
        public List<DokumentDoKsiegowania> Included { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> IncludedNew { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> IncludedPayrollException { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> SkippedNotNew { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> PartialAmortization { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> EmployeeBillsWithSubject { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> InternalDocuments { get; } = new List<DokumentDoKsiegowania>();

        public int Total =>
            Included.Count +
            SkippedNotNew.Count +
            PartialAmortization.Count +
            EmployeeBillsWithSubject.Count +
            InternalDocuments.Count;
    }

    public static class WaitingRoomDocumentFilter
    {
        private static readonly string[] EmployeeBillPropertyNames =
        {
            "RachunekDoUmowyPracowniczej",
            "SkladkiZUSRachunkuDoUmowyPracowniczej",
            "ZaliczkaRachunkuDoUmowyPracowniczej"
        };

        // Rodzaje dokumentów generowanych wewnętrznie przez moduły Rachmistrza (ZUS/rozliczenia właścicielskie,
        // operacje bankowe/kasowe, zapisy VAT, różnice kursowe, rozliczenia międzyokresowe, kompensaty, cesje,
        // delegacje, noty, korekty niezapłaconych dokumentów itd.) - nigdy nie trafiają do auto-dekretacji,
        // niezależnie od tego czy są "nowe" względem baseline'u. Amortyzacja (SrodkiTrwale_*) i pozycje płacowe
        // (ListaPlac*/RachunekDoUmowyPracowniczej*) mają własne, wcześniejsze reguły i NIE wchodzą na tę listę.
        private static readonly HashSet<RodzajDokumentuDoKsiegowania> WewnetrzneRodzajeDokumentow = new HashSet<RodzajDokumentuDoKsiegowania>
        {
            RodzajDokumentuDoKsiegowania.RozliczenieWlascicielskie,
            RodzajDokumentuDoKsiegowania.OperacjaKasowaWplyw,
            RodzajDokumentuDoKsiegowania.OperacjaKasowaWyplyw,
            RodzajDokumentuDoKsiegowania.RaportKasowy,
            RodzajDokumentuDoKsiegowania.OperacjaBankowa_Wplata,
            RodzajDokumentuDoKsiegowania.OperacjaBankowa_Wyplata,
            RodzajDokumentuDoKsiegowania.WyciagBankowy,
            RodzajDokumentuDoKsiegowania.DyspozycjaBankowa,
            RodzajDokumentuDoKsiegowania.Kompensata,
            RodzajDokumentuDoKsiegowania.DokumentCesji,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVAT_Sprzedaz,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVAT_Zakup,
            RodzajDokumentuDoKsiegowania.ZbiorZapisowWEwidencjiVAT,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVAT_KorektaSprzedazyNieudokumentowanej,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVAT_KorektaVATZProporcjiBazowej,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVAT_MarzaZakup,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVAT_MarzaSprzedaz,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVAT_KorektaVATZPreproporcji,
            RodzajDokumentuDoKsiegowania.ZapisWEwidencjiVATOSS_Sprzedaz,
            RodzajDokumentuDoKsiegowania.ZbiorZapisowWEwidencjiVATOSS,
            RodzajDokumentuDoKsiegowania.NaliczenieRoznicKursowychDodatniaRoznica,
            RodzajDokumentuDoKsiegowania.NaliczenieRoznicKursowychUjemnaRoznica,
            RodzajDokumentuDoKsiegowania.NaliczenieRozliczeniaMiedzyokresowego,
            RodzajDokumentuDoKsiegowania.ZbiorczeNaliczenieRozliczeniaMiedzyokresowego,
            RodzajDokumentuDoKsiegowania.DelegacjaKrajowa,
            RodzajDokumentuDoKsiegowania.DelegacjaZagraniczna,
            RodzajDokumentuDoKsiegowania.NotaKsiegowa,
            RodzajDokumentuDoKsiegowania.NotaOdsetkowa,
            RodzajDokumentuDoKsiegowania.Remanent,
            RodzajDokumentuDoKsiegowania.DowodWewnetrzny,
            RodzajDokumentuDoKsiegowania.RaportOkresowy,
            RodzajDokumentuDoKsiegowania.RaportDobowy,
            RodzajDokumentuDoKsiegowania.NaliczenieKosztuEksploatacjiPojazdu,
            RodzajDokumentuDoKsiegowania.KorektaKUPNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.PonowneNaliczenieKUPNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.KorektaVATZakupuNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.PonowneNaliczenieVATZakupuNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.KorektaVATSprzedazyNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.PonowneNaliczenieVATSprzedazyNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.KorektaPodstawyOpodatkowaniaNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.PonowneNaliczeniePodstawyOpodatkowaniaNiezaplaconegoDokumentu,
            RodzajDokumentuDoKsiegowania.RozliczenieSprzedazy,
            RodzajDokumentuDoKsiegowania.RozliczenieZakupu,
            RodzajDokumentuDoKsiegowania.PomniejszenieSprzedazyDetalicznej,
            RodzajDokumentuDoKsiegowania.ZbiorczeNaliczenieZwrotow
        };

        /// <summary>
        /// Klasyfikuje dokumenty z puli StatusKsiegowy==2 do dekretacji. Zastępuje zawodny znacznik "Nowy" ze
        /// Sfery: "nowość" jest teraz określana przez brak numeru dokumentu (Nr - to jest realny klucz główny
        /// DokumentDoKsiegowania, nie Id-GUID) w zapisanym baseline'ie NexoBridge (<see cref="PoczekalniaBaselineStore"/>),
        /// nie przez okno czasowe SDK, które okazało się niestabilne (jeden dokument z datą przyszłą potrafił
        /// przesunąć punkt odniesienia i zablokować dekretację wszystkich innych oczekujących dokumentów).
        /// </summary>
        public static WaitingRoomDocumentSelection SelectForBaseline(
            IEnumerable<DokumentDoKsiegowania> documents,
            Func<DokumentDoKsiegowania, bool> czyZnanyZBaseline)
        {
            if (czyZnanyZBaseline == null)
            {
                throw new InvalidOperationException("Nie udało się zainicjalizować resolvera baseline'u poczekalni. Dekretacja została przerwana, żeby nie wybrać dokumentów po błędnym kryterium.");
            }

            var selection = new WaitingRoomDocumentSelection();
            var allDocuments = (documents ?? Enumerable.Empty<DokumentDoKsiegowania>()).Where(d => d != null).ToList();

            foreach (var document in allDocuments)
            {
                if (CzyCzastkowaAmortyzacja(document))
                {
                    selection.PartialAmortization.Add(document);
                    continue;
                }

                if (CzyListaPlacLubListaRachunkow(document) || CzyRachunekDoUmowyPracowniczejBezPodmiotu(document))
                {
                    selection.Included.Add(document);
                    selection.IncludedPayrollException.Add(document);
                    continue;
                }

                if (CzyRachunekDoUmowyPracowniczejZPodmiotem(document))
                {
                    selection.EmployeeBillsWithSubject.Add(document);
                    continue;
                }

                if (CzyDokumentWewnetrznyRachmistrza(document))
                {
                    selection.InternalDocuments.Add(document);
                    continue;
                }

                if (!czyZnanyZBaseline(document))
                {
                    selection.Included.Add(document);
                    selection.IncludedNew.Add(document);
                    continue;
                }

                selection.SkippedNotNew.Add(document);
            }

            return selection;
        }

        public static bool CzyDokumentWewnetrznyRachmistrza(DokumentDoKsiegowania dokument)
        {
            var rodzaj = PobierzRodzajDokumentuDoKsiegowania(dokument);
            return rodzaj.HasValue && WewnetrzneRodzajeDokumentow.Contains(rodzaj.Value);
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

        public static bool CzyListaPlacLubListaRachunkow(DokumentDoKsiegowania dokument)
        {
            var rodzaj = PobierzRodzajDokumentuDoKsiegowania(dokument);
            if (!rodzaj.HasValue) return false;

            switch (rodzaj.Value)
            {
                case RodzajDokumentuDoKsiegowania.ListaPlac:
                case RodzajDokumentuDoKsiegowania.ListaPlacBezSkladekIZaliczek:
                case RodzajDokumentuDoKsiegowania.ListaRachunkow:
                case RodzajDokumentuDoKsiegowania.ListaRachunkowBezSkladekIZaliczek:
                    return true;
                default:
                    return false;
            }
        }

        public static bool CzyRachunekDoUmowyPracowniczej(DokumentDoKsiegowania dokument)
        {
            var rodzaj = PobierzRodzajDokumentuDoKsiegowania(dokument);
            if (rodzaj.HasValue)
            {
                return rodzaj.Value == RodzajDokumentuDoKsiegowania.RachunekDoUmowyPracowniczej ||
                       rodzaj.Value == RodzajDokumentuDoKsiegowania.RachunekDoUmowyPracowniczejBezSkladekIZaliczek;
            }

            return PobierzRachunkiPracownicze(dokument).Any();
        }

        public static bool CzyRachunekDoUmowyPracowniczejBezPodmiotu(DokumentDoKsiegowania dokument)
        {
            if (!CzyRachunekDoUmowyPracowniczej(dokument)) return false;
            return !MaPodmiot(dokument) && !PobierzRachunkiPracownicze(dokument).Any(MaPodmiot);
        }

        public static bool CzyRachunekDoUmowyPracowniczejZPodmiotem(DokumentDoKsiegowania dokument)
        {
            if (!CzyRachunekDoUmowyPracowniczej(dokument)) return false;
            return MaPodmiot(dokument) || PobierzRachunkiPracownicze(dokument).Any(MaPodmiot);
        }

        public static RodzajDokumentuDoKsiegowania? PobierzRodzajDokumentuDoKsiegowania(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return null;

            try
            {
                var typ = dokument.TypDokumentuDoKsiegowania;
                if (typ == null) return null;

                return (RodzajDokumentuDoKsiegowania)typ.RodzajDokumentuDoKsiegowania;
            }
            catch
            {
                return null;
            }
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

        private static object PobierzWlasciwosc(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            try
            {
                var property = source.GetType().GetProperty(propertyName);
                if (property != null) return property.GetValue(source);

                var field = source.GetType().GetField(propertyName);
                return field?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        public static string DescribeSelection(WaitingRoomDocumentSelection wybor, IEnumerable<DokumentDoKsiegowania> skippedNotNew, int maxDocuments = 30)
        {
            var skipped = (skippedNotNew ?? Enumerable.Empty<DokumentDoKsiegowania>()).Take(maxDocuments).ToList();
            if (skipped.Count == 0) return "brak pominiętych dokumentów";

            var szczegoly = skipped.Select(d => $"{InvoiceDocumentMatcher.Describe(d)}: Nr={DescribeNr(d)}");
            return $"pominieteJakoNieznaneWBaseline=[{string.Join(" || ", szczegoly)}]";
        }

        private static string DescribeNr(DokumentDoKsiegowania dokument)
        {
            try
            {
                return dokument?.Nr.ToString();
            }
            catch
            {
                return "brak";
            }
        }
    }
}
