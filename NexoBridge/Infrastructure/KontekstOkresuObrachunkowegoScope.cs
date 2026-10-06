using System;
using System.Reflection;
using InsERT.Moria.Ksiegowosc;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;

namespace NexoBridge.Infrastructure
{
    /// <summary>
    /// Na czas jednej operacji ustawia okres obrachunkowy w KONTEKŚCIE SESJI Sfery (BusinessContext,
    /// widziany przez obiekty biznesowe jako IKontekstOkresuObrachunkowego), a w Dispose przywraca
    /// poprzedni stan - okres i datę systemową.
    ///
    /// DLACZEGO: w programie ten kontekst to rok wybrany na górnym pasku okna, więc zawsze jest
    /// ustawiony. W sesji otwieranej przez NexoBridge nikt go nie ustawia i zostaje null. Większości
    /// operacji to nie przeszkadza, ale ZapisWEPBO.WalidujPoprawnoscKwotyWWaluciePozycjiZapisu dla
    /// zapisu EP rodzaju "Wynajem" ze stawką 8,5%/12,5% (data od 2022) sprawdza limit 100 000 zł
    /// najmu i sumuje najem w roku wziętym WŁAŚNIE z tego kontekstu, a nie z daty dokumentu - przy
    /// nullu leci ArgumentNullException('okresObrachunkowy') z OkresObrachunkowyExtension.DataPoczatkowa.
    /// Incydent 2026-10-05, COR-CAD (Koroblewski): schemat "Sprzedaż 8,5%" przestawiony 2026-09-09 na
    /// rodzaj "Wynajem" i FS 28/2026 nie dał się zadekretować w 4 kolejnych jobach, a ręcznie w
    /// Rachmistrzu przeszedł od razu. Ustalone z IL Sfery (ZapisWEPBO, BusinessContext), nie zgadywane.
    ///
    /// DLACZEGO PRZYWRACAMY: setter BusinessContext.OkresObrachunkowy (dla Rachmistrza/Rewizora)
    /// przestawia też datę systemową sesji, jeśli ta wypada poza ustawianym okresem (na "teraz" albo
    /// na początek/koniec okresu). Przy rozliczaniu grudnia w styczniu zmieniłoby to np. datę
    /// wystawienia JPK w dalszych etapach joba - więc po operacji wracamy do stanu sprzed niej.
    /// Setter po cichu IGNORUJE okres, którego forma księgowości nie pasuje do produktu sesji - stąd
    /// odczyt kontrolny po ustawieniu.
    /// </summary>
    internal sealed class KontekstOkresuObrachunkowegoScope : IDisposable
    {
        private readonly IKontekstOkresuObrachunkowego _kontekst;
        private readonly OkresObrachunkowy _poprzedniOkres;
        private readonly PropertyInfo _dataSystemowa;
        private readonly DateTime? _poprzedniaDataSystemowa;
        private readonly ILogger _logger;
        private bool _doPrzywrocenia;

        private KontekstOkresuObrachunkowegoScope(
            IKontekstOkresuObrachunkowego kontekst,
            OkresObrachunkowy poprzedniOkres,
            PropertyInfo dataSystemowa,
            DateTime? poprzedniaDataSystemowa,
            ILogger logger)
        {
            _kontekst = kontekst;
            _poprzedniOkres = poprzedniOkres;
            _dataSystemowa = dataSystemowa;
            _poprzedniaDataSystemowa = poprzedniaDataSystemowa;
            _logger = logger;
        }

        /// <summary>Czy kontekst sesji wskazuje teraz żądany okres (ustawiony przez nas albo już wcześniej).</summary>
        public bool Aktywny { get; private set; }

        public static KontekstOkresuObrachunkowegoScope Ustaw(Uchwyt sfera, OkresObrachunkowy okres, ILogger logger)
        {
            if (okres == null)
            {
                logger.LogWarning("[KONTEKST OKRESU] Brak okresu obrachunkowego do ustawienia w kontekście sesji - operacja pójdzie bez kontekstu.");
                return new KontekstOkresuObrachunkowegoScope(null, null, null, null, logger);
            }

            IKontekstOkresuObrachunkowego kontekst;
            try
            {
                kontekst = sfera.PodajObiektTypu<IKontekstOkresuObrachunkowego>();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[KONTEKST OKRESU] Nie udało się pobrać kontekstu okresu obrachunkowego z sesji Sfery - operacja pójdzie bez kontekstu.");
                return new KontekstOkresuObrachunkowegoScope(null, null, null, null, logger);
            }

            if (kontekst == null)
            {
                logger.LogWarning("[KONTEKST OKRESU] Sfera nie zwróciła kontekstu okresu obrachunkowego - operacja pójdzie bez kontekstu.");
                return new KontekstOkresuObrachunkowegoScope(null, null, null, null, logger);
            }

            OkresObrachunkowy poprzedni = kontekst.OkresObrachunkowy;
            PropertyInfo dataSystemowa = kontekst.GetType().GetProperty("DataSystemowa", BindingFlags.Instance | BindingFlags.Public);
            if (dataSystemowa?.PropertyType != typeof(DateTime) || !dataSystemowa.CanRead)
            {
                dataSystemowa = null;
            }

            DateTime? poprzedniaData = dataSystemowa != null ? (DateTime)dataSystemowa.GetValue(kontekst) : null;
            var scope = new KontekstOkresuObrachunkowegoScope(kontekst, poprzedni, dataSystemowa, poprzedniaData, logger);

            if (poprzedni != null && poprzedni.Id == okres.Id)
            {
                scope.Aktywny = true;
                return scope;
            }

            try
            {
                kontekst.OkresObrachunkowy = okres;
                scope._doPrzywrocenia = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[KONTEKST OKRESU] Sfera odrzuciła ustawienie okresu {Okres} w kontekście sesji - operacja pójdzie bez kontekstu.", Opisz(okres));
                return scope;
            }

            scope.Aktywny = kontekst.OkresObrachunkowy?.Id == okres.Id;
            DateTime? nowaData = dataSystemowa != null ? (DateTime)dataSystemowa.GetValue(kontekst) : null;

            if (scope.Aktywny)
            {
                logger.LogInformation("[KONTEKST OKRESU] Ustawiono okres {Okres} w kontekście sesji (poprzednio: {Poprzedni}; data systemowa: {DataPrzed} -> {DataPo}).",
                    Opisz(okres), Opisz(poprzedni), poprzedniaData?.ToString("yyyy-MM-dd") ?? "brak", nowaData?.ToString("yyyy-MM-dd") ?? "brak");
            }
            else if (okres.FormaKsiegowosci == (byte)FormaKsiegowosci.EP)
            {
                logger.LogWarning("[KONTEKST OKRESU] Sfera nie przyjęła okresu {Okres} (FormaKsiegowosci={Forma}). Operacja pójdzie bez kontekstu; zapisy EP rodzaju 'Wynajem' mogą się nie zadekretować.",
                    Opisz(okres), (FormaKsiegowosci)okres.FormaKsiegowosci);
            }
            else
            {
                // Sesja importu to zawsze produkt Rachmistrz, który przyjmuje tylko okresy KPiR/EP - okres ksiąg
                // rachunkowych (Rewizor) setter po cichu ignoruje. Dla takich baz to stan sprzed poprawki, nie problem.
                logger.LogInformation("[KONTEKST OKRESU] Okres {Okres} (FormaKsiegowosci={Forma}) nie pasuje do produktu sesji - kontekst bez zmian, jak dotychczas.",
                    Opisz(okres), (FormaKsiegowosci)okres.FormaKsiegowosci);
            }

            return scope;
        }

        public void Dispose()
        {
            if (!_doPrzywrocenia)
            {
                return;
            }

            _doPrzywrocenia = false;
            try
            {
                // Najpierw data, potem okres: setter daty systemowej sam dobiera okres obowiązujący w
                // nowej dacie, więc przywrócony na końcu okres musi mieć ostatnie słowo.
                if (_dataSystemowa?.CanWrite == true && _poprzedniaDataSystemowa.HasValue
                    && (DateTime)_dataSystemowa.GetValue(_kontekst) != _poprzedniaDataSystemowa.Value)
                {
                    _dataSystemowa.SetValue(_kontekst, _poprzedniaDataSystemowa.Value);
                }

                if (_kontekst.OkresObrachunkowy?.Id != _poprzedniOkres?.Id)
                {
                    _kontekst.OkresObrachunkowy = _poprzedniOkres;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KONTEKST OKRESU] Nie udało się przywrócić kontekstu sesji (okres: {Okres}, data systemowa: {Data}).",
                    Opisz(_poprzedniOkres), _poprzedniaDataSystemowa?.ToString("yyyy-MM-dd") ?? "brak");
            }
        }

        private static string Opisz(OkresObrachunkowy okres)
        {
            if (okres == null) return "brak";
            string nazwa = string.IsNullOrWhiteSpace(okres.Nazwa) ? $"Id={okres.Id}" : okres.Nazwa;
            return okres.Okres != null
                ? $"{nazwa} ({okres.Okres.DataPoczatkowa:yyyy-MM-dd} - {okres.Okres.DataKoncowa:yyyy-MM-dd})"
                : nazwa;
        }
    }
}
