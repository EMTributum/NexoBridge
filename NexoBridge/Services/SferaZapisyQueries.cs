using InsERT.Moria.ModelDanych;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NexoBridge.Services
{
    /// <summary>
    /// Zapisy KPiR/EP/VAT z danego roku pobierane zapytaniem z filtrem daty po stronie SQL (Dane.Wszystkie(String[])
    /// zwraca typowane IQueryable). Wcześniej backfill czytał całe tabele ze wszystkich lat: VAT nie ma
    /// PobierzZapisyZOkresu(DateTime, DateTime), a ostatnie Wszystkie(String[0]) i tak skanowało wszystko.
    ///
    /// Filtr odpowiada BackfillService.ResolveYear (sprawdzone w NexoBridgeKonsola --backfill-query-test, różnica 0):
    /// KPiR/EP - DataZdarzenia (NOT NULL, pierwsza istniejąca ścieżka DatePaths); VAT - DataDokumentu (tylko
    /// korekty, NULL w pozostałych) albo DataWystawienia (NOT NULL). Warunek VAT jest nadzbiorem - dokładny rok
    /// i tak rozstrzyga ResolveYear w BuildRow.
    /// </summary>
    internal static class SferaZapisyQueries
    {
        // TypDokumentuDoKsiegowania.RodzajDokumentuDoKsiegowania (enum RodzajDokumentuDoKsiegowania rzutowany na byte):
        // "Faktura VAT zakupu" i "Korekta faktury VAT zakupu" - wartości potwierdzone w bazach spółek.
        private const byte RodzajFakturaZakupu = 22;
        private const byte RodzajKorektaFakturyZakupu = 24;

        /// <summary>Zwraca null, gdy Dane.Wszystkie(String[]) nie dało oczekiwanego IQueryable.</summary>
        public static List<object> ZapisyZRoku(object manager, string entityType, int year)
        {
            if (manager == null || !SferaReflectionHelpers.TryReadPropertyPath(manager, "Dane", out object dane) || dane == null)
            {
                return null;
            }

            object zapytanie = dane.GetType().GetMethod("Wszystkie", new[] { typeof(string[]) })
                ?.Invoke(dane, new object[] { Array.Empty<string>() });
            var od = new DateTime(year, 1, 1);
            var doWylacznie = od.AddYears(1);

            switch ((entityType ?? string.Empty).ToUpperInvariant())
            {
                case "KPIR":
                    return (zapytanie as IQueryable<ZapisWKPiR>)?
                        .Where(z => z.DataZdarzenia >= od && z.DataZdarzenia < doWylacznie)
                        .ToList().Cast<object>().ToList();
                case "EP":
                    return (zapytanie as IQueryable<ZapisWEP>)?
                        .Where(z => z.DataZdarzenia >= od && z.DataZdarzenia < doWylacznie)
                        .ToList().Cast<object>().ToList();
                case "VAT":
                    return (zapytanie as IQueryable<ZapisWEwidencjiVAT>)?
                        .Where(z => (z.DataDokumentu >= od && z.DataDokumentu < doWylacznie)
                            || (z.DataWystawienia >= od && z.DataWystawienia < doWylacznie))
                        .ToList().Cast<object>().ToList();
                case "DK":
                    // Dekrety Rewizora - tylko te, które są fakturami (sprawdzone na Nexo_EMTRIBUTUM i SQL-em na kilku
                    // spółkach): z zapisem VAT, albo faktura/korekta zakupu bez VAT, albo ręczny dekret bez dokumentu do
                    // księgowania w rejestrze faktur (RK*/FZ*). Wyciągi, kasa, listy płac, magazyn, paragony i PK odpadają -
                    // ich numery (np. numer wyciągu "67") mogłyby przypadkiem trafić w fakturę o tym samym numerze.
                    return (zapytanie as IQueryable<DokumentKsiegowy>)?
                        .Where(d => d.DataDowodu >= od && d.DataDowodu < doWylacznie)
                        .Where(d => d.ZapisWEwidencjiVAT != null
                            || (d.DokumentDoKsiegowania != null
                                && (d.DokumentDoKsiegowania.TypDokumentuDoKsiegowania.RodzajDokumentuDoKsiegowania == RodzajFakturaZakupu
                                    || d.DokumentDoKsiegowania.TypDokumentuDoKsiegowania.RodzajDokumentuDoKsiegowania == RodzajKorektaFakturyZakupu))
                            || (d.DokumentDoKsiegowania == null
                                && (d.RejestrKsiegowy.Symbol.StartsWith("RK") || d.RejestrKsiegowy.Symbol.StartsWith("FZ"))))
                        .ToList().Cast<object>().ToList();
                default:
                    return null;
            }
        }

        /// <summary>Awaryjnie cała tabela (rok rozstrzyga ResolveYear w BuildRow).</summary>
        public static IEnumerable<object> Wszystkie(object manager)
        {
            if (manager == null || !SferaReflectionHelpers.TryReadPropertyPath(manager, "Dane", out object dane) || dane == null)
            {
                return Enumerable.Empty<object>();
            }

            return (dane.GetType().GetMethod("Wszystkie", new[] { typeof(string[]) })
                ?.Invoke(dane, new object[] { Array.Empty<string>() }) as System.Collections.IEnumerable)?
                .Cast<object>()
                .Where(item => item != null)
                ?? Enumerable.Empty<object>();
        }
    }
}
