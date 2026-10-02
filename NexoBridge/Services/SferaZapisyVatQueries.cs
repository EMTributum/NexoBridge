using InsERT.Moria.ModelDanych;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NexoBridge.Services
{
    /// <summary>
    /// Zapytania o zapisy VAT wykonywane po stronie Sfery zamiast iteracji po całej ewidencji w pamięci.
    /// ZapisyWEwidencjiVATDane ma tylko Wszystkie(String[]) zwracające IQueryable&lt;ZapisWEwidencjiVAT&gt;
    /// (potwierdzone w NexoBridgeKonsola --comment-read-test), więc filtr na relacji z dokumentem do
    /// księgowania trafia do SQL, a nie do pętli po wszystkich zapisach VAT bazy klienta.
    /// </summary>
    internal static class SferaZapisyVatQueries
    {
        /// <summary>Zapisy VAT powiązane z dokumentem do księgowania (Zrodlowy/DocelowyDokumentDoKsiegowania).</summary>
        public static List<ZapisWEwidencjiVAT> PowiazaneZDokumentem(object mgrVat, Guid dokumentDoKsiegowaniaId, int limit = 2)
        {
            IQueryable<ZapisWEwidencjiVAT> zapytanie = WszystkieZapytanie(mgrVat);
            if (zapytanie == null || dokumentDoKsiegowaniaId == Guid.Empty)
            {
                return new List<ZapisWEwidencjiVAT>();
            }

            return zapytanie
                .Where(z => z.ZrodlowyDokumentDoKsiegowania.Id == dokumentDoKsiegowaniaId
                    || z.DocelowyDokumentDoKsiegowania.Id == dokumentDoKsiegowaniaId)
                .Take(limit)
                .ToList();
        }

        /// <summary>Cała ewidencja VAT (materializowana) - tylko dla dopasowań, których nie da się wyrazić w SQL.</summary>
        public static List<ZapisWEwidencjiVAT> Wszystkie(object mgrVat)
        {
            return WszystkieZapytanie(mgrVat)?.ToList() ?? new List<ZapisWEwidencjiVAT>();
        }

        private static IQueryable<ZapisWEwidencjiVAT> WszystkieZapytanie(object mgrVat)
        {
            if (mgrVat == null || !SferaReflectionHelpers.TryReadPropertyPath(mgrVat, "Dane", out object dane))
            {
                return null;
            }

            var wszystkie = dane.GetType().GetMethod("Wszystkie", new[] { typeof(string[]) });
            return wszystkie?.Invoke(dane, new object[] { Array.Empty<string>() }) as IQueryable<ZapisWEwidencjiVAT>;
        }
    }
}
