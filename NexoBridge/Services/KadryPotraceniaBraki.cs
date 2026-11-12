using System;
using System.Collections.Generic;

namespace NexoBridge.Services
{
    /// <summary>Dane potrzebne do ustalenia braków w potrąceniu komorniczym (bez zależności od bazy - łatwe do testowania).</summary>
    public sealed class KadryPotraceniaBrakiDane
    {
        public DateTime MonthStart;
        public string OrganNazwa;
        public bool OrganMaAdres;
        public string PracownikPesel;
        /// <summary>Pracownik ma adres zamieszkania (adres pełny albo miejscowość).</summary>
        public bool PracownikMaAdres;
        public string PracodawcaNazwa;
        public string PracodawcaNip;
        /// <summary>Pracodawca ma adres (adres pełny albo miejscowość).</summary>
        public bool PracodawcaMaAdres;
        public string SygnaturaSprawy;
        /// <summary>"w_miesiacu" / "ostatnia_przed_miesiacem" / "brak".</summary>
        public string UmowaZrodlo;
        public bool ZatrudnionyWMiesiacu;
        /// <summary>"o pracę" / "cywilnoprawna" / inne.</summary>
        public string UmowaTypPrawny;
        public bool MaDateRozwiazania;
        public bool MaDateKoncaUmowy;
        public string Stanowisko;
        public string WymiarEtatu;
        public bool MaPodstawe;
        /// <summary>Początek zatrudnienia: najwcześniejsza data początkowa umowy z ciągu (umowa + aneksy).</summary>
        public DateTime? ZatrudnienieOd;
        /// <summary>Indeks 0 = miesiąc przed okresem, 1 = dwa miesiące przed, 2 = trzy; true = jest brutto z zatwierdzonych dokumentów.</summary>
        public bool[] BruttoMiesiace = new bool[3];
    }

    /// <summary>
    /// Reguły pola "braki_danych" potrącenia komorniczego. Zależą od sytuacji pracownika w miesiącu:
    ///  - ZAWSZE: organ_nazwa (brak nazwy organu), organ_adres (brak adresu organu), sygnatura_sprawy.
    ///    Numer konta organu NIE jest brakiem (pismo go nie zawiera).
    ///    Dane osób potrzebne w każdym piśmie, niezależnie od sytuacji pracownika: pracownik_pesel, pracownik_adres
    ///    (brak adresu pełnego i miejscowości), pracodawca_nazwa, pracodawca_nip, pracodawca_adres.
    ///  - Brak jakiejkolwiek umowy (umowa_zrodlo = "brak"): tylko "umowa", nic więcej z części umowy/wynagrodzenia.
    ///  - Jest umowa, ale pracownik niezatrudniony w miesiącu: wymagana tylko data zakończenia umowy
    ///    (data rozwiązania albo data końca umowy na czas określony) - brak = "data_rozwiazania".
    ///  - Zatrudniony na umowę o pracę: stanowisko, wymiar_etatu, wynagrodzenie_podstawa oraz brutto.
    ///  - Zatrudniony na umowę cywilnoprawną: wynagrodzenie_podstawa (kwota z umowy; 0 = brak) oraz brutto (z rachunków);
    ///    bez stanowiska i etatu.
    ///  - "brutto": z zatwierdzonych dokumentów za każdy z ostatnich N miesięcy przed okresem, gdzie N = liczba PEŁNYCH
    ///    miesięcy od początku zatrudnienia do początku okresu, maksymalnie 3. N = 0 (np. zatrudniony w tym miesiącu)
    ///    oznacza, że brutto nie jest wymagane. Brak daty początku zatrudnienia = N = 3.
    /// </summary>
    public static class KadryPotraceniaBraki
    {
        public const int MaxMiesiecyBrutto = 3;

        /// <summary>Liczba pełnych miesięcy od początku zatrudnienia do początku okresu (0..3).
        /// Zatrudnienie od 2. dnia miesiąca (lub później) liczy się od pierwszego pełnego miesiąca (następnego).</summary>
        public static int WymaganeMiesiaceBrutto(DateTime? zatrudnienieOd, DateTime monthStart)
        {
            if (zatrudnienieOd == null) return MaxMiesiecyBrutto;
            DateTime d = zatrudnienieOd.Value.Date;
            DateTime pierwszyPelny = d.Day == 1 ? d : new DateTime(d.Year, d.Month, 1).AddMonths(1);
            int n = (monthStart.Year - pierwszyPelny.Year) * 12 + (monthStart.Month - pierwszyPelny.Month);
            return Math.Max(0, Math.Min(MaxMiesiecyBrutto, n));
        }

        public static List<string> Oblicz(KadryPotraceniaBrakiDane d)
        {
            var braki = new List<string>();
            if (string.IsNullOrWhiteSpace(d.OrganNazwa)) braki.Add("organ_nazwa");
            if (!d.OrganMaAdres) braki.Add("organ_adres");
            if (string.IsNullOrWhiteSpace(d.SygnaturaSprawy)) braki.Add("sygnatura_sprawy");
            if (string.IsNullOrWhiteSpace(d.PracownikPesel)) braki.Add("pracownik_pesel");
            if (!d.PracownikMaAdres) braki.Add("pracownik_adres");
            if (string.IsNullOrWhiteSpace(d.PracodawcaNazwa)) braki.Add("pracodawca_nazwa");
            if (string.IsNullOrWhiteSpace(d.PracodawcaNip)) braki.Add("pracodawca_nip");
            if (!d.PracodawcaMaAdres) braki.Add("pracodawca_adres");

            if (d.UmowaZrodlo == "brak")
            {
                braki.Add("umowa");
                return braki;
            }

            if (!d.ZatrudnionyWMiesiacu)
            {
                if (!d.MaDateRozwiazania && !d.MaDateKoncaUmowy) braki.Add("data_rozwiazania");
                return braki;
            }

            bool oPrace = d.UmowaTypPrawny == "o pracę";
            bool cywilna = d.UmowaTypPrawny == "cywilnoprawna";
            if (!oPrace && !cywilna) return braki;

            if (oPrace)
            {
                if (string.IsNullOrWhiteSpace(d.Stanowisko)) braki.Add("stanowisko");
                if (string.IsNullOrWhiteSpace(d.WymiarEtatu)) braki.Add("wymiar_etatu");
            }
            if (!d.MaPodstawe) braki.Add("wynagrodzenie_podstawa");

            int n = WymaganeMiesiaceBrutto(d.ZatrudnienieOd, d.MonthStart);
            for (int i = 0; i < n; i++)
            {
                if (d.BruttoMiesiace == null || i >= d.BruttoMiesiace.Length || !d.BruttoMiesiace[i])
                {
                    braki.Add("brutto");
                    break;
                }
            }
            return braki;
        }
    }
}
