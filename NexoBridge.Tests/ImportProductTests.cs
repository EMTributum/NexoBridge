using System;
using System.Reflection;
using System.Text.Json;
using NexoBridge.Models;
using NexoBridge.Services;
using Xunit;

namespace NexoBridge.Tests
{
    public class ImportProductTests
    {
        // Te same opcje, co domyslne opcje JSON minimalnego API ASP.NET (web defaults).
        private static readonly JsonSerializerOptions Web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("Rachmistrz", true)]
        [InlineData("rachmistrz", true)]
        [InlineData("Rewizor", true)]
        [InlineData("rewizor", true)]
        [InlineData("REWIZOR", true)]
        [InlineData("  Rewizor ", true)]
        [InlineData("Gratyfikant", false)]
        [InlineData("Rewizor GT", false)]
        [InlineData("Rewizo", false)]
        [InlineData("Subiekt", false)]
        [InlineData("1", false)]
        public void IsSupportedProduct(string product, bool expected)
            => Assert.Equal(expected, ImportJob.IsSupportedProduct(product));

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("Rachmistrz", false)]
        [InlineData("Gratyfikant", false)]
        [InlineData("Rewizor", true)]
        [InlineData("rewizor", true)]
        [InlineData(" REWIZOR ", true)]
        public void IsRewizor(string product, bool expected)
        {
            Assert.Equal(expected, ImportJob.IsRewizorProduct(product));
            Assert.Equal(expected, new ImportJob { Product = product }.IsRewizor);
        }

        [Theory]
        [InlineData(null, "Rachmistrz")]
        [InlineData("", "Rachmistrz")]
        [InlineData("Rachmistrz", "Rachmistrz")]
        [InlineData("rewizor", "Rewizor")]
        [InlineData(" Rewizor ", "Rewizor")]
        public void ResolveProduct_ZgodnyZIsRewizor(string product, string expected)
        {
            object p = typeof(BackfillService).GetMethod("ResolveProduct").Invoke(null, new object[] { product }); Assert.Equal(expected, p.ToString());
            Assert.Equal(expected == "Rewizor", ImportJob.IsRewizorProduct(product));
        }

        [Fact]
        public void Json_BrakPola_Rachmistrz()
        {
            var job = JsonSerializer.Deserialize<ImportJob>("{\"username\":\"u\",\"databaseName\":\"d\"}", Web);
            Assert.Null(job.Product);
            Assert.False(job.IsRewizor);
            Assert.True(ImportJob.IsSupportedProduct(job.Product));
        }

        [Theory]
        [InlineData("{\"product\":\"rewizor\"}", true)]
        [InlineData("{\"Product\":\"Rewizor\"}", true)]
        [InlineData("{\"PRODUCT\":\"REWIZOR\"}", true)]
        [InlineData("{\"product\":null}", false)]
        [InlineData("{\"product\":\"\"}", false)]
        [InlineData("{\"product\":\"Rachmistrz\"}", false)]
        public void Json_Wartosci(string json, bool rewizor)
        {
            var job = JsonSerializer.Deserialize<ImportJob>(json, Web);
            Assert.True(ImportJob.IsSupportedProduct(job.Product));
            Assert.Equal(rewizor, job.IsRewizor);
        }

        [Fact]
        public void Json_LiczbaZamiastTekstu_RzucaJsonException_czyli_400()
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ImportJob>("{\"product\":1}", Web));
        }

        [Fact]
        public void Json_IsRewizor_NieJestSerializowane_ani_wczytywane()
        {
            string json = JsonSerializer.Serialize(new ImportJob { Product = "Rewizor" }, Web);
            Assert.DoesNotContain("isRewizor", json, StringComparison.OrdinalIgnoreCase);
            var job = JsonSerializer.Deserialize<ImportJob>("{\"isRewizor\":true}", Web);
            Assert.False(job.IsRewizor);
        }

        [Theory]
        [InlineData("WynikowyDokumentKsiegowy", "DK")]
        [InlineData("WynikowyDokumentKsiegowyProxy", "DK")]
        [InlineData("WynikowyZapisWEwidencjiVAT", "Vat")]
        [InlineData("WynikowyZapisWKPiR", "KPiR")]
        [InlineData("WynikowyZapisWEP", "EP")]
        [InlineData("WynikowyDekret", "Nieznany")]
        [InlineData("", "Nieznany")]
        [InlineData(null, "Nieznany")]
        [InlineData("Cokolwiek", "Nieznany")]
        public void KluczMenedzeraDlaTypuWyniku(string typ, string expected)
        {
            var m = typeof(AttachmentService).GetMethod("KluczMenedzeraDlaTypuWyniku", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(m);
            Assert.Equal(expected, (string)m.Invoke(null, new object[] { typ }));
        }
    }
}
