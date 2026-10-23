using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.KontrolaSkarbowa;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>
    /// Podgląd podatków (tax-preview) JEDNEGO klienta, w JEDNEJ sesji Sfery (Rachmistrz) - wołany
    /// wyłącznie w procesie potomnym "--tax-preview-client-worker" (Program.RunTaxPreviewClientWorker).
    ///
    /// WSZYSTKO TYLKO DO ODCZYTU - nigdzie nie ma Zapisz():
    ///   - VAT: świeże IDeklaracja.Wylicz(MojaFirma, wzorzec JPK_V7M/V7K, od, do, okres) - bez zapisu,
    ///     niezależnie od tego, czy JPK za okres został już wygenerowany (decyzja: zawsze świeże),
    ///   - VAT-UE: tylko status (płatnik, metoda, czy deklaracja już istnieje),
    ///   - PIT: świeże Wylicz zaliczki per wspólnik - bez zapisu,
    ///   - amortyzacja: istniejące odpisy (zaksięgowane / zapisane-niezaksięgowane) + opcjonalny podgląd
    ///     NaliczAmortyzacje bez Zapisz (TAX_PREVIEW_PENDING_AMORTIZATION),
    ///   - ZUS właściciela: ZusOwnerContributionExtractor (odczyt już policzonych naliczeń).
    ///
    /// Port prototypu NexoBridgeKonsola (--tax-preview), sprawdzonego na 16 bazach: liczniki operacji
    /// SQL pokazały zapisy wyłącznie w mox.application_session (sesja frameworka), a świeże kwoty zgodne
    /// ze złożonymi deklaracjami (różnice tylko tam, gdzie księgi zmieniono po złożeniu).
    ///
    /// BO deklaracji NIE są Dispose'owane (jak PitCalculationService.UtworzDeklaracjePit) - Dispose BO
    /// deklaracji zamyka ObjectContext managera; sesja i tak kończy się razem z procesem potomnym.
    /// </summary>
    internal static class TaxPreviewCalculator
    {
        private const string TypDeklaracjiVatUe = "VAT-UE";

        private sealed class Kontekst
        {
            public Uchwyt Sfera { get; set; }
            public object MojaFirma { get; set; }
            public object MgrOkresyVat { get; set; }
            public List<object> OkresyVat { get; set; } = new List<object>();
            public object MgrWersji { get; set; }
            public List<object> WersjeDeklaracji { get; set; } = new List<object>();
            public List<object> Wspolnicy { get; set; } = new List<object>();
            public List<object> OkresyObrachunkowe { get; set; } = new List<object>();
            public TypAmortyzacji TypAmortyzacjiPodatkowy { get; set; }
            public List<object> SrodkiTrwale { get; set; } = new List<object>();
            public List<object> OperacjeAm { get; set; } = new List<object>();
            public List<string> Bledy { get; set; } = new List<string>();
        }

        public static List<TaxPreviewPeriodResult> Compute(Uchwyt sfera, TaxPreviewWorkerRequest request, bool podgladAmortyzacjiWlaczony)
        {
            ZaladujZnaneAssemblySfery();
            Kontekst ctx = ZbudujKontekst(sfera);

            var wyniki = new List<TaxPreviewPeriodResult>();
            foreach (TaxPreviewPeriodRequest okres in request.Periods.OrderBy(p => p.Year).ThenBy(p => p.Month))
            {
                if (request.SoftDeadlineUtc.HasValue && DateTimeOffset.UtcNow > request.SoftDeadlineUtc.Value)
                {
                    wyniki.Add(new TaxPreviewPeriodResult
                    {
                        Year = okres.Year,
                        Month = okres.Month,
                        Status = "SKIPPED_DEADLINE",
                        Error = "Przekroczono miękki limit czasu procesu potomnego - okres pominięty."
                    });
                    continue;
                }

                wyniki.Add(PoliczOkres(ctx, new DateTime(okres.Year, okres.Month, 1), podgladAmortyzacjiWlaczony && okres.IncludePendingAmortization));
            }

            return wyniki;
        }

        // ---------------------------------------------------------------------------------------------
        // Kontekst - raz na proces potomny (wspólnicy, okresy VAT, wersje deklaracji, ŚT, odpisy)
        // ---------------------------------------------------------------------------------------------

        private static Kontekst ZbudujKontekst(Uchwyt sfera)
        {
            var ctx = new Kontekst { Sfera = sfera };

            object mgrPodmioty = Menedzer(ctx, "IPodmioty");
            try
            {
                ctx.MojaFirma = mgrPodmioty == null ? null : ((dynamic)mgrPodmioty).ZnajdzMojaFirme()?.Dane;
            }
            catch (Exception ex)
            {
                ctx.Bledy.Add($"IPodmioty.ZnajdzMojaFirme(): {Opisz(ex)}");
            }

            ctx.MgrOkresyVat = Menedzer(ctx, "IOkresyRozliczenVAT");
            ctx.OkresyVat = Wszystkie(ctx, ctx.MgrOkresyVat, "IOkresyRozliczenVAT");

            ctx.MgrWersji = Menedzer(ctx, "IWersjeDeklaracji");
            try
            {
                ctx.WersjeDeklaracji = ctx.MgrWersji == null
                    ? new List<object>()
                    : ((IEnumerable)((dynamic)ctx.MgrWersji).Wersje).Cast<object>().ToList();
            }
            catch (Exception ex)
            {
                ctx.Bledy.Add($"IWersjeDeklaracji.Wersje: {Opisz(ex)}");
            }

            // Wspólnik = podmiot z Osoba.Wspolnik.OkresyRozliczenPIT (jak PitCalculationService.cs:46-57).
            foreach (object podmiot in Wszystkie(ctx, mgrPodmioty, "IPodmioty"))
            {
                if (TryReadPropertyPath(podmiot, "Osoba.Wspolnik.OkresyRozliczenPIT", out object okresyPit) && okresyPit != null)
                {
                    ctx.Wspolnicy.Add(podmiot);
                }
            }

            ctx.OkresyObrachunkowe = Wszystkie(ctx, Menedzer(ctx, "IOkresyObrachunkowe"), "IOkresyObrachunkowe");

            ctx.TypAmortyzacjiPodatkowy = Wszystkie(ctx, Menedzer(ctx, "ITypyAmortyzacji"), "ITypyAmortyzacji")
                .OfType<TypAmortyzacji>()
                .FirstOrDefault(t => string.Equals(t.Nazwa, "Podatkowy", StringComparison.OrdinalIgnoreCase));
            ctx.SrodkiTrwale = Wszystkie(ctx, Menedzer(ctx, "ISrodkiTrwale"), "ISrodkiTrwale");
            ctx.OperacjeAm = Wszystkie(ctx, Menedzer(ctx, "IOperacjeAM"), "IOperacjeAM");

            return ctx;
        }

        private static object Menedzer(Kontekst ctx, string interfejs)
        {
            try
            {
                object menedzer = GetManagerByInterfaceName(ctx.Sfera, interfejs);
                if (menedzer == null)
                {
                    ctx.Bledy.Add($"Brak menedżera {interfejs}.");
                }

                return menedzer;
            }
            catch (Exception ex)
            {
                ctx.Bledy.Add($"Menedżer {interfejs}: {Opisz(ex)}");
                return null;
            }
        }

        /// <summary>Dane.Wszystkie() przez dynamic - refleksja GetMethod("Wszystkie") na typie
        /// konkretnym nie widzi tej metody (sprawdzone w Konsoli: po cichu zwracała null).</summary>
        private static List<object> Wszystkie(Kontekst ctx, object menedzer, string etykieta)
        {
            if (menedzer == null)
            {
                return new List<object>();
            }

            try
            {
                object dane = ((dynamic)menedzer).Dane;
                return ((IEnumerable)((dynamic)dane).Wszystkie()).Cast<object>().Where(x => x != null).ToList();
            }
            catch (Exception ex)
            {
                ctx.Bledy.Add($"{etykieta}.Dane.Wszystkie(): {Opisz(ex)}");
                return new List<object>();
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Okres
        // ---------------------------------------------------------------------------------------------

        private static TaxPreviewPeriodResult PoliczOkres(Kontekst ctx, DateTime miesiac, bool podgladAmortyzacji)
        {
            var wynik = new TaxPreviewPeriodResult
            {
                Year = miesiac.Year,
                Month = miesiac.Month,
                ComputedAt = DateTimeOffset.Now,
                Vat = PoliczVat(ctx, miesiac),
                VatUe = PoliczVatUe(ctx, miesiac),
                Pit = PoliczPit(ctx, miesiac),
                Amortization = PoliczAmortyzacje(ctx, miesiac, podgladAmortyzacji),
                Zus = PoliczZus(ctx, miesiac),
            };

            var statusy = new List<string> { wynik.Vat.Status, wynik.VatUe.Status, wynik.Amortization.Status, wynik.Zus.Status };
            statusy.AddRange(wynik.Pit.Select(p => p.Status));
            int bledne = statusy.Count(s => s == "FAILED");
            wynik.Status = bledne == 0 ? "SUCCESS" : bledne == statusy.Count ? "FAILED" : "PARTIAL_SUCCESS";
            if (ctx.Bledy.Count > 0 && bledne > 0)
            {
                wynik.Error = string.Join(" | ", ctx.Bledy.Take(5));
            }

            return wynik;
        }

        // --- VAT -------------------------------------------------------------------------------------

        private static TaxPreviewVat PoliczVat(Kontekst ctx, DateTime miesiac)
        {
            var vat = new TaxPreviewVat();
            try
            {
                DateTime dataOd = miesiac;
                DateTime dataDo = miesiac.AddMonths(1).AddDays(-1);

                // Ta sama reguła co VatCalculationService.ZnajdzOkresVatKrajowyDlaOkresu (Rodzaj 1 = krajowy).
                object okresVat = ctx.OkresyVat
                    .Where(o => ReadIntCandidate(o, "Rodzaj") == 1)
                    .Where(o => ReadDateCandidate(o, "Poczatek") is not DateTime poczatek || poczatek <= dataDo)
                    .OrderByDescending(o => ReadDateCandidate(o, "Poczatek") ?? DateTime.MinValue)
                    .ThenByDescending(o => ReadIntCandidate(o, "Id") ?? 0)
                    .FirstOrDefault();

                if (okresVat == null)
                {
                    vat.Settlement = "none";
                    return vat;
                }

                int metoda = ReadIntCandidate(okresVat, "Metoda") ?? 0;
                vat.CashMethod = ReadBoolCandidate(okresVat, "MetodaKasowa");

                string etykieta;
                if (metoda == 4)
                {
                    vat.Settlement = "exempt";
                    return vat;
                }

                if (metoda == 1)
                {
                    vat.Settlement = "monthly";
                    etykieta = "V7M";
                }
                else if (metoda == 2)
                {
                    vat.Settlement = "quarterly";
                    if (miesiac.Month % 3 != 0)
                    {
                        vat.DueInPeriod = false;
                        return vat;
                    }

                    dataOd = miesiac.AddMonths(-2);
                    etykieta = "V7K";
                }
                else
                {
                    vat.Status = "FAILED";
                    vat.Error = $"Nieobsługiwana metoda rozliczeń VAT (Kod: {metoda}).";
                    return vat;
                }

                vat.DueInPeriod = true;

                if (ctx.MojaFirma == null)
                {
                    vat.Status = "FAILED";
                    vat.Error = "Nie udało się odczytać Mojej firmy.";
                    return vat;
                }

                object wersja = WersjaDeklaracjiSelector.Wybierz(ctx.MgrWersji, ctx.WersjeDeklaracji, nazwa => nazwa.Contains(etykieta), dataDo, out _);
                vat.TemplateName = ReadStringCandidate(wersja, "Nazwa");
                if (!TryReadPropertyPath(wersja, "Id", out object idWersji) || idWersji is not Guid wersjaId)
                {
                    vat.Status = "FAILED";
                    vat.Error = $"Brak wzorca JPK_{etykieta}.";
                    return vat;
                }

                object okresObrachunkowy = ZnajdzOkresObrachunkowy(ctx, dataDo, out string ostrzezenie);
                vat.Warning = ostrzezenie;

                object dane = WyliczBezZapisu(ctx, "IDeklaracjeSkarbowe", "IDeklaracje", ctx.MojaFirma, wersjaId, dataOd, dataDo, okresObrachunkowy);

                // Kolejność pól jak VatCalculationService.WypelnijKwotyDeklaracyjnieAwaryjnie.
                decimal? doZaplaty = ReadDecimalCandidate(dane, "KwotaZobowiazania");
                if (doZaplaty is null or 0m)
                {
                    doZaplaty = ReadDecimalCandidate(dane, "KwotaDoZaplaty") ?? doZaplaty;
                }

                vat.AmountToPay = doZaplaty;
                vat.AmountToCarryOver = ReadDecimalCandidate(dane, "KwotaDoPrzeniesienia");
                vat.AmountToRefund = ZwrotZZapisanegoJpk(ctx, etykieta, dataOd, dataDo);
            }
            catch (Exception ex)
            {
                vat.Status = "FAILED";
                vat.Error = Opisz(ex);
            }

            return vat;
        }

        /// <summary>P_54 (zwrot na rachunek) z najnowszego zapisanego JPK_V7 za okres - jak
        /// VatCalculationService.ZnajdzJpkV7 + WypelnijKwotyZPowiazanejDeklaracji (prototyp: Konsola TpReadSavedJpk).
        /// Tylko odczyt; błąd nie psuje sekcji VAT (null = nie wiadomo, kwota zostaje "do przeniesienia").</summary>
        private static decimal? ZwrotZZapisanegoJpk(Kontekst ctx, string etykieta, DateTime dataOd, DateTime dataDo)
        {
            try
            {
                object mgrJpk = GetManagerByInterfaceName(ctx.Sfera, "IJednolitePlikiKontrolne");
                object daneJpk = mgrJpk == null ? null : ((dynamic)mgrJpk).Dane;
                if (daneJpk == null)
                {
                    return null;
                }

                int rodzajOczekiwany = Convert.ToInt32(etykieta == "V7K" ? RodzajJPK.V7K : RodzajJPK.V7M);
                object jpk = ((IEnumerable)((dynamic)daneJpk).ZnajdzWysylkeVATRozliczeniowaWOkresie(dataOd, dataDo))
                    .Cast<object>()
                    .Where(j => TryReadPropertyPath(j, "Rodzaj", out object rodzaj) && rodzaj != null && Convert.ToInt32(rodzaj) == rodzajOczekiwany)
                    .OrderByDescending(j => ReadIntCandidate(j, "Id") ?? 0)
                    .FirstOrDefault();
                if (jpk == null)
                {
                    return null;
                }

                foreach (object czesc in ReadObjectCollection(jpk, "CzesciDeklaracyjne"))
                {
                    TryReadPropertyPath(czesc, "Deklaracja", out object deklaracja);
                    if (ReadDeclarationFieldAmount(deklaracja, "54") is decimal zwrot)
                    {
                        return zwrot;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        // --- VAT-UE ----------------------------------------------------------------------------------

        private static TaxPreviewVatUe PoliczVatUe(Kontekst ctx, DateTime miesiac)
        {
            var vatUe = new TaxPreviewVatUe();
            try
            {
                DateTime dataOd = miesiac;
                DateTime dataDo = miesiac.AddMonths(1).AddDays(-1);

                object rodzajVatUe = UtworzWartoscEnum("RodzajRozliczenVAT", "VAT_UE");
                object daneOkresow = ctx.MgrOkresyVat == null ? null : ((dynamic)ctx.MgrOkresyVat).Dane;
                object okres = rodzajVatUe == null || daneOkresow == null
                    ? null
                    : WywolajMetode(daneOkresow, "ZnajdzObowiazujacyWDniu", rodzajVatUe, dataDo);

                if (okres == null)
                {
                    vatUe.IsVatUePayer = false;
                    return vatUe;
                }

                vatUe.IsVatUePayer = true;
                int metoda = ReadIntCandidate(okres, "Metoda") ?? 0;
                vatUe.SettlementMethod = metoda switch { 101 => "monthly", 102 => "quarterly", _ => $"kod {metoda}" };
                if (metoda == 102)
                {
                    if (miesiac.Month % 3 != 0)
                    {
                        vatUe.DueInPeriod = false;
                        return vatUe;
                    }

                    dataOd = miesiac.AddMonths(-2);
                }

                vatUe.DueInPeriod = true;
                if (ctx.MojaFirma != null)
                {
                    object mgrDeklaracji = GetManagerByInterfaceName(ctx.Sfera, "IDeklaracje") ?? GetManagerByInterfaceName(ctx.Sfera, "IDeklaracjeSkarbowe");
                    object daneDeklaracji = mgrDeklaracji == null ? null : ((dynamic)mgrDeklaracji).Dane;
                    object istniejace = daneDeklaracji == null
                        ? null
                        : WywolajMetode(daneDeklaracji, "ZnajdzDeklaracjeWOkresie", TypDeklaracjiVatUe, dataOd, dataDo, ctx.MojaFirma);
                    vatUe.DeclarationExists = istniejace is IEnumerable lista && lista.Cast<object>().Any();
                }
            }
            catch (Exception ex)
            {
                vatUe.Status = "FAILED";
                vatUe.Error = Opisz(ex);
            }

            return vatUe;
        }

        // --- PIT -------------------------------------------------------------------------------------

        private static List<TaxPreviewPit> PoliczPit(Kontekst ctx, DateTime miesiac)
        {
            var wyniki = new List<TaxPreviewPit>();
            DateTime dataKoniec = miesiac.AddMonths(1).AddDays(-1);

            foreach (object wspolnik in ctx.Wspolnicy)
            {
                var pit = new TaxPreviewPit { PartnerName = NazwaWspolnika(wspolnik) };
                wyniki.Add(pit);
                try
                {
                    TryReadPropertyPath(wspolnik, "Osoba.Wspolnik.OkresyRozliczenPIT", out object okresyPit);
                    object aktywnyOkres = (okresyPit as IEnumerable)?.Cast<object>()
                        .Where(o => ReadDateCandidate(o, "Poczatek") is DateTime poczatek && miesiac >= poczatek)
                        .OrderByDescending(o => ReadDateCandidate(o, "Poczatek"))
                        .FirstOrDefault();

                    if (aktywnyOkres == null)
                    {
                        pit.Status = "SKIPPED";
                        pit.Warning = "Brak aktywnego okresu podatkowego w dacie rozliczenia.";
                        continue;
                    }

                    int formaId = ReadIntCandidate(aktywnyOkres, "FormaOpodatkowania") ?? 0; // 1 liniowy, 2 skala, 3 ryczałt
                    int metodaId = ReadIntCandidate(aktywnyOkres, "MetodaOplacaniaZaliczek") ?? 0; // 0 miesięcznie, 1 kwartalnie
                    pit.Form = formaId switch { 1 => "liniowy", 2 => "skala", 3 => "ryczałt", _ => $"kod {formaId}" };
                    pit.Method = metodaId == 0 ? "monthly" : "quarterly";

                    DateTime dataStart = miesiac;
                    if (metodaId != 0)
                    {
                        if (miesiac.Month % 3 != 0)
                        {
                            pit.DueInPeriod = false;
                            continue;
                        }

                        dataStart = miesiac.AddMonths(-2);
                    }

                    pit.DueInPeriod = true;

                    // Filtr nazw wzorców jak PitCalculationService.cs:111-137; wybór wersji - wg daty.
                    string rdzen = metodaId == 0 ? "Zaliczka miesięczna" : "Zaliczka kwartalna";
                    Func<string, bool> filtr = nazwa => formaId switch
                    {
                        2 => nazwa.StartsWith($"{rdzen} PIT") && !nazwa.Contains("liniow") && !nazwa.Contains("ryczałt"),
                        1 => nazwa.StartsWith(rdzen) && nazwa.Contains("liniow"),
                        3 => nazwa.StartsWith($"{rdzen} ryczałtowa PIT"),
                        _ => false,
                    };

                    object wersja = WersjaDeklaracjiSelector.Wybierz(ctx.MgrWersji, ctx.WersjeDeklaracji, filtr, dataKoniec, out _);
                    pit.TemplateName = ReadStringCandidate(wersja, "Nazwa");
                    if (!TryReadPropertyPath(wersja, "Id", out object idWersji) || idWersji is not Guid wersjaId)
                    {
                        pit.Status = "FAILED";
                        pit.Error = "Nie znaleziono wzorca formularza MF dla tej formy opodatkowania.";
                        continue;
                    }

                    object okresObrachunkowy = ZnajdzOkresObrachunkowy(ctx, dataKoniec, out string ostrzezenie);
                    pit.Warning = ostrzezenie;

                    object dane = WyliczBezZapisu(ctx, "IDeklaracje", "IDeklaracjeSkarbowe", wspolnik, wersjaId, dataStart, dataKoniec, okresObrachunkowy);
                    pit.AmountDue = ReadDecimalCandidate(dane, "KwotaZobowiazania") ?? ReadDecimalCandidate(dane, "Wartosc");
                }
                catch (Exception ex)
                {
                    pit.Status = "FAILED";
                    pit.Error = Opisz(ex);
                }
            }

            return wyniki;
        }

        /// <summary>Jak PitCalculationService.PobierzNazwe - klucz zgodny z wierszami PIT z eksportu.</summary>
        private static string NazwaWspolnika(object podmiot)
        {
            string nazwa = ReadStringCandidate(podmiot, "NazwaSkrocona", "Osoba.Nazwisko");
            return string.IsNullOrEmpty(nazwa) ? "Nieznany Wspólnik" : nazwa;
        }

        // --- Amortyzacja -----------------------------------------------------------------------------

        private static TaxPreviewAmortization PoliczAmortyzacje(Kontekst ctx, DateTime miesiac, bool podglad)
        {
            var am = new TaxPreviewAmortization();
            try
            {
                DateTime dataOd = miesiac;
                DateTime dataDo = miesiac.AddMonths(1).AddDays(-1);

                if (ctx.TypAmortyzacjiPodatkowy == null)
                {
                    // Brak typu "Podatkowy" = brak ewidencji ŚT w tej bazie - nie błąd.
                    am.Warning = ctx.SrodkiTrwale.Count == 0 ? null : "Brak typu amortyzacji 'Podatkowy'.";
                    return am;
                }

                int typId = ctx.TypAmortyzacjiPodatkowy.Id;
                var odpisy = ctx.OperacjeAm
                    .Where(o => ReadDateCandidate(o, "Data") is DateTime data && data.Date >= dataOd && data.Date <= dataDo)
                    .Where(o => ReadIntCandidate(o, "TypAmortyzacji.Id") == typId)
                    .ToList();
                am.OperationsInMonth = odpisy.Count;

                var srodkiZRata = new HashSet<int>();
                foreach (object odpis in odpisy)
                {
                    // Eksport bierze najpierw Wartosc (AmortizationService.cs:104-106) - to samo pole.
                    decimal wartosc = ReadDecimalCandidate(odpis, "Wartosc", "WartoscStanowiacaKoszty") ?? 0m;
                    if (CzyZaksiegowany(odpis, "DokumentDoKsiegowania.StatusKsiegowy") || CzyZaksiegowany(odpis, "OperacjaAMZ.DokumentDoKsiegowania.StatusKsiegowy"))
                    {
                        am.Booked += wartosc;
                    }
                    else
                    {
                        am.SavedNotBooked += wartosc;
                    }

                    if (ReadIntCandidate(odpis, "SrodekTrwaly.Id") is int stId)
                    {
                        srodkiZRata.Add(stId);
                    }
                }

                am.PendingComputed = podglad;
                if (podglad)
                {
                    PoliczPodgladRat(ctx, dataDo, srodkiZRata, am);
                }

                am.Total = am.Booked + am.SavedNotBooked + am.Pending;
            }
            catch (Exception ex)
            {
                am.Status = "FAILED";
                am.Error = Opisz(ex);
            }

            return am;
        }

        /// <summary>Jak AmortizationService.ObliczAmortyzacjeAsync, ale BEZ amBO.Zapisz(). Domyślnie
        /// wyłączone (TAX_PREVIEW_PENDING_AMORTIZATION) - ścieżka z ratą &gt; 0 bez zapisu czeka na
        /// sprawdzenie na WARSZTATOWNI.</summary>
        private static void PoliczPodgladRat(Kontekst ctx, DateTime dataDo, HashSet<int> srodkiZRata, TaxPreviewAmortization am)
        {
            object mgrAm = GetManagerByInterfaceName(ctx.Sfera, "IOperacjeAM");
            if (mgrAm == null)
            {
                am.Warning = "Brak menedżera IOperacjeAM - pomijam podgląd niezaksięgowanych rat.";
                return;
            }

            int pominiete = 0;
            foreach (object st in ctx.SrodkiTrwale)
            {
                if (ReadIntCandidate(st, "Id") is int stId && srodkiZRata.Contains(stId))
                {
                    continue;
                }

                using (dynamic amBO = ((dynamic)mgrAm).Utworz())
                {
                    try
                    {
                        amBO.NaliczAmortyzacje((dynamic)st, ctx.TypAmortyzacjiPodatkowy, dataDo);
                        decimal kwota = 0m;
                        try { kwota = (decimal)amBO.Dane.Wartosc; } catch { }
                        try { if (kwota == 0m) kwota = (decimal)amBO.Dane.WartoscStanowiacaKoszty; } catch { }
                        if (kwota > 0m)
                        {
                            am.Pending += kwota;
                        }
                    }
                    catch
                    {
                        // Środek zamortyzowany / rata już istnieje / brak planu - tak jak w eksporcie.
                        pominiete++;
                    }
                }
            }

            if (pominiete > 0)
            {
                am.Warning = $"Pominięto {pominiete} środków trwałych w podglądzie rat.";
            }
        }

        private static bool CzyZaksiegowany(object odpis, string sciezkaStatusu)
        {
            int? status = ReadIntCandidate(odpis, sciezkaStatusu);
            return status.HasValue && status.Value == (int)StatusKsiegowyDokumentuDoKsiegowania.Zaksiegowany;
        }

        // --- ZUS -------------------------------------------------------------------------------------

        private static TaxPreviewZus PoliczZus(Kontekst ctx, DateTime miesiac)
        {
            var zus = new TaxPreviewZus();
            try
            {
                zus.Entries = ZusOwnerContributionExtractor.GetOwnerContributionsForPeriod(ctx.Sfera, miesiac.Year, miesiac.Month);
            }
            catch (Exception ex)
            {
                zus.Status = "FAILED";
                zus.Error = Opisz(ex);
            }

            return zus;
        }

        // ---------------------------------------------------------------------------------------------
        // Wspólne
        // ---------------------------------------------------------------------------------------------

        /// <summary>Utworz() + Wylicz(podmiot, wersja, od, do, okres) - BEZ Zapisz() i BEZ Dispose().
        /// Za każdym razem świeży manager; ponowienie przy ObjectDisposedException (jak
        /// PitCalculationService.UtworzDeklaracjePit).</summary>
        private static object WyliczBezZapisu(Kontekst ctx, string menedzer, string menedzerZapasowy,
            object podmiot, Guid wersjaId, DateTime dataOd, DateTime dataDo, object okresObrachunkowy)
        {
            object bo;
            try
            {
                bo = UtworzDeklaracje(ctx, menedzer, menedzerZapasowy);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex.GetBaseException() is ObjectDisposedException)
            {
                bo = UtworzDeklaracje(ctx, menedzer, menedzerZapasowy);
            }

            MethodInfo wylicz = bo.GetType().GetMethods()
                .Concat(bo.GetType().GetInterfaces().SelectMany(i => i.GetMethods()))
                .FirstOrDefault(m => m.Name == "Wylicz" && m.GetParameters().Length == 5);

            if (wylicz == null)
            {
                throw new MissingMethodException(bo.GetType().FullName, "Wylicz(Podmiot, Guid, DateTime, DateTime, OkresObrachunkowy)");
            }

            try
            {
                wylicz.Invoke(bo, new[] { podmiot, wersjaId, dataOd, dataDo, okresObrachunkowy });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            return ((dynamic)bo).Dane;
        }

        private static object UtworzDeklaracje(Kontekst ctx, string menedzer, string menedzerZapasowy)
        {
            object mgr = GetManagerByInterfaceName(ctx.Sfera, menedzer) ?? GetManagerByInterfaceName(ctx.Sfera, menedzerZapasowy);
            if (mgr == null)
            {
                throw new InvalidOperationException($"Brak menedżera {menedzer}/{menedzerZapasowy}.");
            }

            return ((dynamic)mgr).Utworz();
        }

        /// <summary>Okres obrachunkowy zawierający datę (jak AccountingService.PobierzOkresObrachunkowy,
        /// ale bez zgadywania "ostatniego okresu"). Gdy go nie ma (np. firma działa od tego roku, a liczymy
        /// grudzień poprzedniego), Wylicz dostaje null - tak jak w eksporcie; w Konsoli oba warianty dawały
        /// identyczne kwoty.</summary>
        private static object ZnajdzOkresObrachunkowy(Kontekst ctx, DateTime data, out string ostrzezenie)
        {
            object okres = ctx.OkresyObrachunkowe
                .Where(o => ReadDateCandidate(o, "Okres.DataPoczatkowa") is DateTime od
                    && ReadDateCandidate(o, "Okres.DataKoncowa") is DateTime @do
                    && od.Date <= data.Date && @do.Date >= data.Date)
                .OrderByDescending(o => ReadDateCandidate(o, "Okres.DataPoczatkowa"))
                .FirstOrDefault();

            ostrzezenie = okres == null
                ? $"Brak okresu obrachunkowego zawierającego {data:yyyy-MM-dd} - wyliczono bez wskazania okresu."
                : null;
            return okres;
        }

        private static object UtworzWartoscEnum(string nazwaTypu, string wartosc)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] typy;
                try { typy = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { typy = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                Type typ = typy.FirstOrDefault(t => t != null && t.IsEnum && t.Name == nazwaTypu);
                if (typ != null)
                {
                    try { return Enum.Parse(typ, wartosc, ignoreCase: true); }
                    catch { return null; }
                }
            }

            return null;
        }

        /// <summary>Wywołuje metodę o pasujących typach argumentów (szuka także w interfejsach).</summary>
        private static object WywolajMetode(object cel, string nazwa, params object[] argumenty)
        {
            Type typ = cel.GetType();
            MethodInfo metoda = typ.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Concat(typ.GetInterfaces().SelectMany(i => i.GetMethods()))
                .FirstOrDefault(m => m.Name == nazwa
                    && m.GetParameters().Length == argumenty.Length
                    && m.GetParameters().Select((p, i) => argumenty[i] == null
                        ? !p.ParameterType.IsValueType
                        : p.ParameterType.IsInstanceOfType(argumenty[i])).All(x => x));

            if (metoda == null)
            {
                throw new MissingMethodException(typ.FullName, $"{nazwa}({argumenty.Length})");
            }

            try
            {
                return metoda.Invoke(cel, argumenty);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>W świeżym procesie potomnym assembly Sfery bywają jeszcze niezaładowane, a
        /// GetManagerByInterfaceName szuka tylko w załadowanych (jak VatCalculationService.ZaladujZnaneAssemblySfery).</summary>
        private static void ZaladujZnaneAssemblySfery()
        {
            foreach (string nazwa in new[]
            {
                "InsERT.Moria.Deklaracje",
                "InsERT.Moria.EwidencjaVAT",
                "InsERT.Moria.KontrolaSkarbowa",
                "InsERT.Moria.Klienci",
                "InsERT.Moria.SrodkiTrwale",
                "InsERT.Moria.Ksiegowosc"
            })
            {
                try { Assembly.Load(nazwa); }
                catch { }
            }
        }

        private static string Opisz(Exception ex)
        {
            Exception wewnetrzny = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            Exception bazowy = wewnetrzny.GetBaseException();
            return $"{bazowy.GetType().Name}: {bazowy.Message}";
        }
    }
}
