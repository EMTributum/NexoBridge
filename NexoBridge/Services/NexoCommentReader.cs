using InsERT.Moria.ModelDanych;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
            public string TypPowiazanego { get; set; }
            public string NazwaPowiazanego { get; set; }

            /// <summary>Zapis potwierdzony odwrotnie (komentarz znaleziony wśród komentarzy tego zapisu).</summary>
            public int? ConfirmedEntityId { get; set; }

            public bool ContainsUrl(string url)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return false;
                }

                return (ZserializowanaTresc ?? string.Empty).Contains(url, StringComparison.OrdinalIgnoreCase)
                    || (Tresc ?? string.Empty).Contains(url, StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>Id zapisu, do którego komentarz jest podpięty, wg klucza menedżera (KPiR/EP/Vat).</summary>
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

                return ZapisKsiegowyId ?? ZapisVatId;
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

        /// <summary>
        /// Id zapisów (KPiR/EP przez ZapisKsiegowy, VAT przez BazowyZapisWEwidencjiVAT), które mają już aktywny
        /// komentarz zawierający <paramref name="urlMarker"/> - jedno zapytanie na bazę zamiast odczytu
        /// komentarzy rekord po rekordzie (backfill). Zwraca null, gdy zapytania nie da się wykonać.
        /// </summary>
        public static (HashSet<int> ZapisyKsiegowe, HashSet<int> ZapisyVat) EntityIdsWithLink(object komentarzeManager, string urlMarker, out string error)
        {
            object dane = GetData(komentarzeManager, out error);
            if (dane == null)
            {
                return (null, null);
            }

            try
            {
                var wszystkie = dane.GetType().GetMethod("Wszystkie", new[] { typeof(string[]) })
                    ?.Invoke(dane, new object[] { Array.Empty<string>() }) as IQueryable<KomentarzNexo>;
                if (wszystkie == null)
                {
                    error = "Dane.Wszystkie(String[]) nie zwróciło IQueryable<KomentarzNexo>";
                    return (null, null);
                }

                var powiazania = wszystkie
                    .Where(k => !k.Usuniety && (k.Tresc.Contains(urlMarker) || k.ZserializowanaTresc.Contains(urlMarker)))
                    .Select(k => new
                    {
                        ZapisKsiegowyId = (int?)k.ZapisKsiegowy.Id,
                        ZapisVatId = (int?)k.BazowyZapisWEwidencjiVAT.Id
                    })
                    .ToList();

                var ksiegowe = new HashSet<int>(powiazania.Where(p => p.ZapisKsiegowyId.HasValue).Select(p => p.ZapisKsiegowyId.Value));
                var vat = new HashSet<int>(powiazania.Where(p => p.ZapisVatId.HasValue).Select(p => p.ZapisVatId.Value));
                return (ksiegowe, vat);
            }
            catch (Exception ex)
            {
                error = $"zapytanie o komentarze z linkiem: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
                return (null, null);
            }
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
                TypPowiazanego = SferaReflectionHelpers.ReadStringCandidate(komentarz, "TypPowiazanego"),
                NazwaPowiazanego = SferaReflectionHelpers.ReadStringCandidate(komentarz, "NazwaPowiazanego")
            };
        }
    }
}
