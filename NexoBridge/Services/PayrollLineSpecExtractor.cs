using System;
using System.Collections.Generic;
using System.Linq;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.OperacjeZewnetrzne;
using NexoBridge.Models;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>Przedział ilościowy jednej pozycji cennika - wyciągnięty z obiektu Sfery na dane
    /// proste, żeby dało się go bezpiecznie przenieść między sesją biura (gdzie żyje konfiguracja
    /// cennika) a sesją Gratyfikanta konkretnego klienta (gdzie dopiero wywołujemy licznik).</summary>
    internal sealed record PayrollTierSpec(int From, int To, decimal? UnitNet, decimal? UnitGross, decimal? CollectiveNet, decimal? CollectiveGross);

    /// <summary>Jedna pozycja cennika kadrowo-płacowego z podpiętym licznikiem obiektów - gotowa do
    /// policzenia w sesji Gratyfikanta klienta (patrz PayrollLineSpecExtractor.ComputeLine).</summary>
    internal sealed record PendingPayrollLineSpec(string Label, Guid CounterGuid, List<PayrollTierSpec> Tiers);

    /// <summary>Wszystkie oczekujące pozycje kadrowo-płacowe jednego klienta biura, plus jego NIP/nazwa
    /// do zbudowania raportu.</summary>
    internal sealed record ClientPayrollSpec(string Nip, string Name, List<PendingPayrollLineSpec> Lines);

    /// <summary>
    /// Faza 4 planu billingu (naprawa 2026-09-22 - patrz ZNANY BŁĄD w historii BillingConfigurationService.cs):
    /// konfiguracja cennika (nazwa pozycji, GUID licznika, przedziały cenowe) żyje w sesji BIURA
    /// (KlientBiura.CennikUslug), ale realne dane do policzenia (rachunki, listy płac) żyją w OSOBNEJ,
    /// własnej bazie Gratyfikant KAŻDEGO klienta - potwierdzone na żywych danych CSPAY.PL: te same
    /// liczniki wywołane z sesji biura dały błędne wartości (1 i 6 zamiast 2 i 0), a wywołane z sesji
    /// połączonej wprost z bazą klienta dały poprawne (2 i 0). Stąd rozdzielenie na dwie fazy:
    /// ExtractPayrollSpec (tania, w sesji biura, bez wołania licznika) i ComputeLine (w osobnej sesji
    /// Gratyfikanta per klient, patrz PayrollCountsService).
    /// </summary>
    internal static class PayrollLineSpecExtractor
    {
        public static ClientPayrollSpec ExtractPayrollSpec(Podmiot client)
        {
            string nip = ReadStringCandidate(client, "NIP", "Nip");
            string name = BillingConfigurationService.GetDisplayName(client);
            var lineSpecs = new List<PendingPayrollLineSpec>();

            // Liczymy KAŻDĄ pozycję cennika z podpiętym licznikiem, niezależnie od nazwy cennika. Wcześniej
            // wymagaliśmy, żeby nazwa cennika zawierała "kadry/płace" - cenniki nazwane od klienta ("WWS",
            // "Nomex_MB") z identycznymi pozycjami kadrowymi były przez to po cichu pomijane (zgłoszenie
            // 2026-10-05). Przegląd wszystkich 135 klientów biura (NexoBillingKonsola --dump-pricing-overview)
            // pokazał, że liczniki są podpięte wyłącznie do "Rachunek do umowy pracowniczej" i "Wypłata wg
            // miesiąca rozliczenia" - to sam licznik mówi, co policzyć, nazwa cennika nic nie wnosi.
            if (TryReadPropertyPath(client, "KlientBiura", out object biuroClient) && biuroClient != null
                && TryReadPropertyPath(biuroClient, "CennikUslug", out object pricing) && pricing != null)
            {
                foreach (object position in ReadObjectCollection(pricing, "PozycjeCennikaUslug"))
                {
                    string counterGuidText = ReadStringCandidate(position, "ObiektPozycjiCennikaUslug.FunkcjaZliczajaca");
                    if (string.IsNullOrWhiteSpace(counterGuidText) || !Guid.TryParse(counterGuidText, out Guid counterGuid) || counterGuid == Guid.Empty)
                    {
                        continue;
                    }

                    List<PayrollTierSpec> tiers = ReadObjectCollection(position, "WartosciPozycjiCennikaUslug")
                        .Select(tier => new PayrollTierSpec(
                            ReadIntCandidate(tier, "Od") ?? 1,
                            ReadIntCandidate(tier, "Do") ?? int.MaxValue,
                            ReadDecimalCandidate(tier, "CenaJednostkowaNetto"),
                            ReadDecimalCandidate(tier, "CenaJednostkowaBrutto"),
                            ReadDecimalCandidate(tier, "CenaZbiorczaWPrzedzialeNetto", "CenaZbiorczaNetto"),
                            ReadDecimalCandidate(tier, "CenaZbiorczaWPrzedzialeBrutto", "CenaZbiorczaBrutto")))
                        .ToList();

                    string label = BillingConfigurationService.GetPositionLabel(position)
                        ?? BillingConfigurationService.GetDefaultServiceName(BillingConfigurationService.MonthlyServiceKind.Payroll);
                    lineSpecs.Add(new PendingPayrollLineSpec(label, counterGuid, tiers));
                }
            }

            return new ClientPayrollSpec(nip, name, lineSpecs);
        }

        /// <summary>Woła licznik obiektów w AKTUALNIE połączonej sesji (musi to być sesja Gratyfikanta
        /// połączona z własną bazą KLIENTA, nie sesja biura) i przelicza wynik na kwotę wg przedziału
        /// ilościowego. Zwraca null, jeśli licznik nie istnieje, zwrócił 0/ujemną ilość, albo żaden
        /// przedział nie pasuje.</summary>
        public static PayrollFeeLineDto ComputeLine(PendingPayrollLineSpec spec, IFabrykaLicznikowObiektow counterFactory, DateTime periodStart, DateTime periodEnd)
        {
            ILicznikObiektow counter = counterFactory.Znajdz(spec.CounterGuid);
            if (counter == null)
            {
                return null;
            }

            int quantity = (int)Math.Round(counter.Zlicz(periodStart, periodEnd), MidpointRounding.AwayFromZero);
            if (quantity <= 0)
            {
                return null;
            }

            foreach (PayrollTierSpec tier in spec.Tiers)
            {
                if (quantity < tier.From || quantity > tier.To)
                {
                    continue;
                }

                if (tier.UnitNet.HasValue || tier.UnitGross.HasValue)
                {
                    // Cena za sztukę - na fakturze ilość x cena jednostkowa (np. 6 szt x 70 zł), nazwa bez "za N".
                    return new PayrollFeeLineDto
                    {
                        Name = spec.Label,
                        Net = tier.UnitNet.HasValue ? tier.UnitNet.Value * quantity : (decimal?)null,
                        Gross = tier.UnitGross.HasValue ? tier.UnitGross.Value * quantity : (decimal?)null,
                        Quantity = quantity,
                        Unit = "szt",
                        UnitNet = tier.UnitNet,
                        UnitGross = tier.UnitGross
                    };
                }

                if (tier.CollectiveNet.HasValue || tier.CollectiveGross.HasValue)
                {
                    return new PayrollFeeLineDto
                    {
                        Name = $"{spec.Label} za {quantity}",
                        Net = tier.CollectiveNet,
                        Gross = tier.CollectiveGross
                    };
                }
            }

            return null;
        }

        /// <summary>Okres [Od, Do) do przekazania do Zlicz() - koniec WYŁĄCZNIE pierwszy dzień
        /// następnego miesiąca (nie ostatni dzień bieżącego), bo liczniki filtrują "dataOd &lt;= X &lt;
        /// dataDo" (przedział otwarty z prawej) - potwierdzone przez ZliczSqlCommandString().</summary>
        public static (DateTime Start, DateTime End) ResolvePeriodRange(int year, int month)
        {
            DateTime start = new DateTime(year, month, 1);
            return (start, start.AddMonths(1));
        }
    }
}
