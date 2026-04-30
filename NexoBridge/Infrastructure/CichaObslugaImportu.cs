using InsERT.Moria.ImportKsiegowy;

namespace NexoBridge.Infrastructure
{
    public class CichaObslugaImportu : IObslugaZdarzenSeryjnegoImportu
    {
        public InterakcjaWOperacjiImportuKsiegowego InteraktywnyTryb { get; } = new InterakcjaWOperacjiImportuKsiegowego();

        public CichaObslugaImportu()
        {
            InteraktywnyTryb.KontynuowacImportKolejnegoDokumentuPoBledzie = (dokument, blad) => true;
            InteraktywnyTryb.SprobujNaprawicNiepoprawneDokumentyWynikowe = (p1, p2, p3, p4, p5, p6, p7) => default(WynikFragmentuOperacjiImportu);
            InteraktywnyTryb.ZapytajOUsuwanieIstniejacych = (dokumenty) => default(WynikFragmentuOperacjiImportu);
        }

        public void RozpoczecieCalosci(int ilosc) { }
        public void ZakonczenieWszystkich() { }
        public void RozpoczeciePojedynczego(ImportSeryjnyEventArgs e) { }
        public void RozpoczecieFragmentuWImporciePojedynczego(ImportSeryjnyEventArgs e) { }
        public void ZakonczeniePojedynczego(ImportSeryjnyEventArgs e) { }
    }
}