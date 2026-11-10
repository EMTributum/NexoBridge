using System.Collections.Generic;

namespace NexoBridge.Models
{
    // Modele synchronicznego odczytu podstaw składek ZUS pracowników z bazy Gratyfikanta - patrz KadryPodstawyZusSqlService.
    // Kwoty jako decimal (JSON number, bez zaokrąglania).

    /// <summary>POST /api/kadry/podstawy-zus</summary>
    public class KadryPodstawyZusRequest
    {
        public string DatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
    }

    public class KadryPodstawyZusResponse
    {
        public string DatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public List<KadryPodstawyZusEmployee> Employees { get; set; } = new();
    }

    /// <summary>Osoba z co najmniej jedną wypłatą albo rachunkiem na liście za miesiąc (ListyWynagrodzen.Miesiac).</summary>
    public class KadryPodstawyZusEmployee
    {
        /// <summary>PracownicyGr.Id - unikalne tylko w obrębie bazy.</summary>
        public int EmployeeId { get; set; }
        public string LastName { get; set; }
        public string FirstName { get; set; }

        /// <summary>Suma podstaw wymiaru składek emerytalnej i rentowej z wypłat (umowy o pracę) za miesiąc.</summary>
        public decimal ContributionBase { get; set; }

        /// <summary>Suma brutto z wypłat (umowy o pracę) za miesiąc - do porównania z podstawą.</summary>
        public decimal Gross { get; set; }

        /// <summary>Suma podstaw z rachunków do umów cywilnoprawnych za miesiąc (nie wchodzi do ContributionBase).</summary>
        public decimal BillsContributionBase { get; set; }

        public int PayoutCount { get; set; }
        public int BillCount { get; set; }

        /// <summary>Co najmniej jedna wypłata lub rachunek osoby jest na niezatwierdzonej liście.</summary>
        public bool HasUnapproved { get; set; }

        /// <summary>KodZUS stopnia niepełnosprawności aktywnego w miesiącu: 1 lekki, 2 umiarkowany, 3 znaczny,
        /// 4 orzeczenie do 16. roku życia; null = brak orzeczenia.</summary>
        public string DisabilityCode { get; set; }

        /// <summary>OkresyNiepelnosprawnosci.SchorzeniaSpecjalne aktywnego okresu.</summary>
        public bool SpecialConditions { get; set; }

        /// <summary>Pełne 6-cyfrowe kody tytułu ubezpieczenia z umów wypłat za miesiąc (np. "011002" = pracownik,
        /// bez prawa do emerytury, stopień umiarkowany) - kadrowe mają je w Excelu i rozróżniają nimi dwa rekordy
        /// tej samej osoby. Zwykle jeden kod.</summary>
        public List<string> InsuranceCodes { get; set; } = new();
    }
}
