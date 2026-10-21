using InsERT.Moria.ModelDanych;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NexoBridge.Services
{
    /// <summary>
    /// Odczyt komentarzy Nexo (IKomentarzeNexo) - wspólny dla audytu załączników i backfillu.
    ///
    /// API potwierdzone na żywym Rachmistrzu (NexoBridgeKonsola --comment-read-test, 2026-10-01):
    /// - IKomentarzeNexo.Dane (KomentarzeNexoDane) ma FindById(int) oraz Wszystkie(EntityDataObjectBase encja),
    ///   czyli komentarze podpięte do danej encji. Bezparametrowego Wszystkie() nie ma, a Wszystkie(String[])
    ///   zwraca wszystkie komentarze bazy (nie używać). Metod PodajKomentarze/PobierzKomentarze/Komentarze/
    ///   ListaKomentarzy, które wcześniej zgadywał kod, Sfera nie wystawia.
    /// - Encja zapisu ma kolekcję KomentarzeNexo (ZapisWKPiR/ZapisWEP z ZapisKsiegowy, ZapisWEwidencjiVAT
    ///   z BazowyZapisWEwidencjiVAT).
    /// - KomentarzNexo wskazuje swój zapis nawigacją ZapisKsiegowy (KPiR/EP) albo BazowyZapisWEwidencjiVAT (VAT),
    ///   ma też Usuniety / IsInRecycleBin oraz Tresc i ZserializowanaTresc (tam zostaje HTML linku).
    /// </summary>
    internal static class NexoCommentReader
    {
        internal sealed class CommentInfo
        {
            public int? Id { get; set; }
            public string Tresc { get; set; }
            public string ZserializowanaTresc { get; set; }
            public bool Deleted { get; set; }
            public int? ZapisKsiegowyId { get; set; }
            public int? ZapisVatId { get; set; }
            /// <summary>Dekret Rewizora (KomentarzNexo.BazowyDokumentKsiegowy).</summary>
            public int? DokumentKsiegowyId { get; set; }
            public string TypPowiazanego { get; set; }
            public string NazwaPowiazanego { get; set; }

            /// <summary>Zapis potwierdzony odwrotnie (komentarz znaleziony wśród komentarzy tego zapisu).</summary>
            public int? ConfirmedEntityId { get; set; }

            public string ViewerUrl => ExtractViewerUrl(Tresc, ZserializowanaTresc);

            public bool PureLink => IsPureLink(Tresc, ZserializowanaTresc);

            public bool ContainsUrl(string url)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return false;
                }

                return (ZserializowanaTresc ?? string.Empty).Contains(url, StringComparison.OrdinalIgnoreCase)
                    || (Tresc ?? string.Empty).Contains(url, StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>Id zapisu, do którego komentarz jest podpięty, wg klucza menedżera (KPiR/EP/Vat/DK).</summary>
            public int? LinkedEntityId(string managerKey)
            {
                if (ConfirmedEntityId.HasValue)
                {
                    return ConfirmedEntityId;
                }

                if (string.Equals(managerKey, "Vat", StringComparison.OrdinalIgnoreCase))
                {
                    return ZapisVatId;
                }

                if (string.Equals(managerKey, "KPiR", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(managerKey, "EP", StringComparison.OrdinalIgnoreCase))
                {
                    return ZapisKsiegowyId;
                }

                if (string.Equals(managerKey, "DK", StringComparison.OrdinalIgnoreCase))
                {
                    return DokumentKsiegowyId;
                }

                return ZapisKsiegowyId ?? ZapisVatId ?? DokumentKsiegowyId;
            }

            public string Describe()
            {
                return $"komentarz#{(Id.HasValue ? Id.Value.ToString() : "brak")} ({TypPowiazanego ?? "?"}: {NazwaPowiazanego ?? "?"}{(Deleted ? ", usunięty" : string.Empty)})";
            }
        }

        public static CommentInfo FindById(object komentarzeManager, int id, out string error)
        {
            error = null;
            object dane = GetData(komentarzeManager, out error);
            if (dane == null)
            {
                return null;
            }

            MethodInfo findById = dane.GetType().GetMethod("FindById", new[] { typeof(int) });
            if (findById == null)
            {
                error = $"{dane.GetType().FullName} nie ma FindById(int)";
                return null;
            }

            try
            {
                object komentarz = findById.Invoke(dane, new object[] { id });
                if (komentarz == null)
                {
                    error = $"FindById({id}) zwrócił null";
                    return null;
                }

                return Describe(komentarz);
            }
            catch (Exception ex)
            {
                error = $"FindById({id}): {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
                return null;
            }
        }

        public static List<CommentInfo> ForEntity(object komentarzeManager, object entity, out string error)
        {
            error = null;
            var result = new List<CommentInfo>();
            if (entity == null)
            {
                error = "brak encji";
                return result;
            }

            object dane = GetData(komentarzeManager, out error);
            object target = UnwrapEntity(dane, entity);
            MethodInfo forEntity = FindWszystkieForEntity(dane, target);

            IEnumerable source = null;
            if (forEntity != null)
            {
                try
                {
                    source = forEntity.Invoke(dane, new[] { target }) as IEnumerable;
                }
                catch (Exception ex)
                {
                    error = $"Dane.Wszystkie(encja): {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
                }
            }

            if (source == null && SferaReflectionHelpers.TryReadPropertyPath(target ?? entity, "KomentarzeNexo", out object collection))
            {
                source = collection as IEnumerable;
            }

            if (source == null)
            {
                error ??= $"brak sposobu odczytu komentarzy dla {(target ?? entity).GetType().FullName}";
                return result;
            }

            try
            {
                foreach (object komentarz in source)
                {
                    if (komentarz != null)
                    {
                        result.Add(Describe(komentarz));
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"odczyt komentarzy encji: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
            }

            return result;
        }

        // Link do podglądu = ścieżka /faktury/{id} (niezależnie od hosta) ORAZ nasza etykieta - sam "/faktury/"
        // łapał też obce linki wklejone przez księgowych (np. portal dostawcy), a takie zapisy backfill pomijał.
        internal const string ViewerUrlMarker = "/faktury/";
        internal const string ViewerLinkLabel = "Podgląd faktury";

        private static readonly Regex ViewerUrlRegex = new(@"https?://[^\s""'<>]+?/faktury/[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex PureTrescRegex = new(@"^Podgląd faktury[^:\r\n]{0,40}:\s*https?://\S+\s*$", RegexOptions.CultureInvariant);

        internal sealed class LinkedCommentIndex
        {
            public Dictionary<int, List<CommentInfo>> ZapisyKsiegowe { get; } = new Dictionary<int, List<CommentInfo>>();
            public Dictionary<int, List<CommentInfo>> ZapisyVat { get; } = new Dictionary<int, List<CommentInfo>>();
            public Dictionary<int, List<CommentInfo>> DokumentyKsiegowe { get; } = new Dictionary<int, List<CommentInfo>>();

            /// <summary>Słownik zapisów danego typu backfillu: VAT, DK (dekret Rewizora), reszta = zapisy księgowe KPiR/EP.</summary>
            public Dictionary<int, List<CommentInfo>> ForEntityType(string entityType)
            {
                return entityType?.ToUpperInvariant() switch
                {
                    "VAT" => ZapisyVat,
                    "DK" => DokumentyKsiegowe,
                    _ => ZapisyKsiegowe
                };
            }
        }

        /// <summary>
        /// Aktywne komentarze z linkiem do podglądu, pogrupowane po zapisie (KPiR/EP przez ZapisKsiegowy, VAT przez
        /// BazowyZapisWEwidencjiVAT) - jedno zapytanie na bazę zamiast odczytu komentarzy rekord po rekordzie
        /// (backfill). Zwraca null, gdy zapytania nie da się wykonać.
        /// </summary>
        public static LinkedCommentIndex LinkedComments(object komentarzeManager, out string error)
        {
            IQueryable<KomentarzNexo> wszystkie = Zapytanie(komentarzeManager, out error);
            if (wszystkie == null)
            {
                return null;
            }

            try
            {
                var indeks = new LinkedCommentIndex();
                foreach (CommentInfo komentarz in Projekcja(wszystkie.Where(k => !k.Usuniety
                    && (k.Tresc.Contains(ViewerUrlMarker) || k.ZserializowanaTresc.Contains(ViewerUrlMarker))
                    && (k.Tresc.Contains(ViewerLinkLabel) || k.ZserializowanaTresc.Contains(ViewerLinkLabel)))))
                {
                    if (komentarz.ZapisKsiegowyId.HasValue)
                    {
                        Dodaj(indeks.ZapisyKsiegowe, komentarz.ZapisKsiegowyId.Value, komentarz);
                    }

                    if (komentarz.ZapisVatId.HasValue)
                    {
                        Dodaj(indeks.ZapisyVat, komentarz.ZapisVatId.Value, komentarz);
                    }

                    if (komentarz.DokumentKsiegowyId.HasValue)
                    {
                        Dodaj(indeks.DokumentyKsiegowe, komentarz.DokumentKsiegowyId.Value, komentarz);
                    }
                }

                return indeks;
            }
            catch (Exception ex)
            {
                error = $"zapytanie o komentarze z linkiem: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
                return null;
            }
        }

        /// <summary>
        /// Komentarze po Id czytane projekcją - wartości idą prosto z bazy, a nie z encji śledzonych przez kontekst
        /// sesji, więc to rzetelny odczyt kontrolny po zapisie w tej samej sesji (sprawdzone w NexoBridgeKonsola
        /// --comment-relink-test na WARSZTATOWNI). Zwraca null, gdy zapytania nie da się wykonać.
        /// </summary>
        public static List<CommentInfo> ReadFromDatabase(object komentarzeManager, IEnumerable<int> ids, out string error)
        {
            IQueryable<KomentarzNexo> wszystkie = Zapytanie(komentarzeManager, out error);
            if (wszystkie == null)
            {
                return null;
            }

            try
            {
                var wynik = new List<CommentInfo>();
                foreach (int[] paczka in (ids ?? Enumerable.Empty<int>()).Distinct().Chunk(500))
                {
                    wynik.AddRange(Projekcja(wszystkie.Where(k => paczka.Contains(k.Id))));
                }

                return wynik;
            }
            catch (Exception ex)
            {
                error = $"odczyt komentarzy po Id: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
                return null;
            }
        }

        public static string ExtractViewerUrl(string tresc, string zserializowanaTresc)
        {
            foreach (string zrodlo in new[] { zserializowanaTresc, tresc })
            {
                if (string.IsNullOrWhiteSpace(zrodlo))
                {
                    continue;
                }

                Match match = ViewerUrlRegex.Match(WebUtility.HtmlDecode(zrodlo));
                if (match.Success)
                {
                    return match.Value.TrimEnd('.', ',', ';', ')');
                }
            }

            return null;
        }

        /// <summary>Komentarz zawiera wyłącznie nasz link ("Podgląd faktury...: URL" i HTML z jednym &lt;a&gt;) -
        /// tylko taki naprawa backfillu może nadpisać, nie ryzykując utraty tekstu dopisanego przez księgowego.</summary>
        public static bool IsPureLink(string tresc, string zserializowanaTresc)
        {
            if (string.IsNullOrWhiteSpace(tresc) || !PureTrescRegex.IsMatch(tresc.Trim()))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(zserializowanaTresc))
            {
                return true;
            }

            string html = WebUtility.HtmlDecode(zserializowanaTresc);
            if (Regex.Matches(html, "href", RegexOptions.IgnoreCase).Count != 1)
            {
                return false;
            }

            string tekst = Regex.Replace(html, "<[^>]+>", string.Empty).Trim();
            return tekst.StartsWith(ViewerLinkLabel, StringComparison.Ordinal) && tekst.Length <= 60;
        }

        private static IQueryable<KomentarzNexo> Zapytanie(object komentarzeManager, out string error)
        {
            object dane = GetData(komentarzeManager, out error);
            if (dane == null)
            {
                return null;
            }

            var wszystkie = dane.GetType().GetMethod("Wszystkie", new[] { typeof(string[]) })
                ?.Invoke(dane, new object[] { Array.Empty<string>() }) as IQueryable<KomentarzNexo>;
            if (wszystkie == null)
            {
                error = "Dane.Wszystkie(String[]) nie zwróciło IQueryable<KomentarzNexo>";
            }

            return wszystkie;
        }

        private static List<CommentInfo> Projekcja(IQueryable<KomentarzNexo> zapytanie)
        {
            return zapytanie
                .Select(k => new
                {
                    k.Id,
                    ZapisKsiegowyId = (int?)k.ZapisKsiegowy.Id,
                    ZapisVatId = (int?)k.BazowyZapisWEwidencjiVAT.Id,
                    DokumentKsiegowyId = (int?)k.BazowyDokumentKsiegowy.Id,
                    k.Usuniety,
                    k.Tresc,
                    k.ZserializowanaTresc
                })
                .ToList()
                .Select(k => new CommentInfo
                {
                    Id = k.Id,
                    ZapisKsiegowyId = k.ZapisKsiegowyId,
                    ZapisVatId = k.ZapisVatId,
                    DokumentKsiegowyId = k.DokumentKsiegowyId,
                    Deleted = k.Usuniety,
                    Tresc = k.Tresc,
                    ZserializowanaTresc = k.ZserializowanaTresc
                })
                .ToList();
        }

        private static void Dodaj(Dictionary<int, List<CommentInfo>> slownik, int zapisId, CommentInfo komentarz)
        {
            if (!slownik.TryGetValue(zapisId, out List<CommentInfo> lista))
            {
                lista = new List<CommentInfo>();
                slownik[zapisId] = lista;
            }

            lista.Add(komentarz);
        }

        private static object GetData(object komentarzeManager, out string error)
        {
            error = null;
            if (komentarzeManager == null)
            {
                error = "brak menedżera IKomentarzeNexo";
                return null;
            }

            if (!SferaReflectionHelpers.TryReadPropertyPath(komentarzeManager, "Dane", out object dane))
            {
                error = "brak IKomentarzeNexo.Dane";
                return null;
            }

            return dane;
        }

        // Wszystkie(EntityDataObjectBase) - wybieramy przeciążenie przyjmujące encję, a nie Wszystkie(String[]).
        private static MethodInfo FindWszystkieForEntity(object dane, object entity)
        {
            if (dane == null || entity == null)
            {
                return null;
            }

            return dane.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Wszystkie"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType != typeof(string[])
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(entity));
        }

        // Menedżery zapisów potrafią zwracać obiekt biznesowy zamiast encji - wtedy encja jest w .Dane.
        private static object UnwrapEntity(object dane, object entity)
        {
            if (dane == null || FindWszystkieForEntity(dane, entity) != null)
            {
                return entity;
            }

            return SferaReflectionHelpers.TryReadPropertyPath(entity, "Dane", out object inner) && FindWszystkieForEntity(dane, inner) != null
                ? inner
                : entity;
        }

        private static CommentInfo Describe(object komentarz)
        {
            return new CommentInfo
            {
                Id = SferaReflectionHelpers.ReadIntCandidate(komentarz, "Id"),
                Tresc = SferaReflectionHelpers.ReadStringCandidate(komentarz, "Tresc"),
                ZserializowanaTresc = SferaReflectionHelpers.ReadStringCandidate(komentarz, "ZserializowanaTresc"),
                Deleted = SferaReflectionHelpers.ReadBoolCandidate(komentarz, "Usuniety") == true
                    || SferaReflectionHelpers.ReadBoolCandidate(komentarz, "IsInRecycleBin") == true,
                ZapisKsiegowyId = SferaReflectionHelpers.ReadIntCandidate(komentarz, "ZapisKsiegowy.Id"),
                ZapisVatId = SferaReflectionHelpers.ReadIntCandidate(komentarz, "BazowyZapisWEwidencjiVAT.Id"),
                DokumentKsiegowyId = SferaReflectionHelpers.ReadIntCandidate(komentarz, "BazowyDokumentKsiegowy.Id"),
                TypPowiazanego = SferaReflectionHelpers.ReadStringCandidate(komentarz, "TypPowiazanego"),
                NazwaPowiazanego = SferaReflectionHelpers.ReadStringCandidate(komentarz, "NazwaPowiazanego")
            };
        }
    }
}
