using System.Globalization;
using NUnit.Framework;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Writer;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class MzPeakIonMobilityTests
    {
        // Minimal ILogEntryAccess so a ScanTrailer can be built from synthetic key/value pairs
        // (small.RAW carries no FAIMS, so the extraction is driven directly here).
        private sealed class FakeLog : ILogEntryAccess
        {
            private readonly string[] _labels, _values;
            public FakeLog(string[] labels, string[] values) { _labels = labels; _values = values; }
            public string[] Labels => _labels;
            public string[] Values => _values;
            public int Length => _labels.Length;
        }

        private static ScanTrailer Trailer(params (string k, string v)[] entries)
        {
            var labels = new string[entries.Length];
            var values = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++) { labels[i] = entries[i].k; values[i] = entries[i].v; }
            return new ScanTrailer(new FakeLog(labels, values));
        }

        [Test]
        public void Faims_On_Populates_CompensationVoltage_And_Type()
        {
            var rec = new MzPeakRecord();
            var trailer = Trailer(
                ("FAIMS Voltage On:", "On"),
                ("FAIMS CV:", (-45.0).ToString(CultureInfo.CurrentCulture)));

            MzPeakSpectrumWriter.ApplyIonMobility(trailer, rec);

            Assert.That(rec.IonMobilityValue, Is.EqualTo(-45.0).Within(1e-9));
            Assert.That(rec.IonMobilityType, Is.EqualTo("MS:1001581"));
        }

        [Test]
        public void Faims_Off_Leaves_IonMobility_Null()
        {
            var rec = new MzPeakRecord();
            var trailer = Trailer(
                ("FAIMS Voltage On:", "Off"),
                ("FAIMS CV:", (-45.0).ToString(CultureInfo.CurrentCulture)));

            MzPeakSpectrumWriter.ApplyIonMobility(trailer, rec);

            Assert.That(rec.IonMobilityValue, Is.Null);
            Assert.That(rec.IonMobilityType, Is.Null);
        }

        [Test]
        public void No_Faims_Trailer_Leaves_IonMobility_Null()
        {
            var rec = new MzPeakRecord();
            MzPeakSpectrumWriter.ApplyIonMobility(Trailer(), rec);

            Assert.That(rec.IonMobilityValue, Is.Null);
            Assert.That(rec.IonMobilityType, Is.Null);
        }

        [Test]
        public void Faims_On_Without_Cv_Leaves_IonMobility_Null()
        {
            var rec = new MzPeakRecord();
            MzPeakSpectrumWriter.ApplyIonMobility(Trailer(("FAIMS Voltage On:", "On")), rec);

            Assert.That(rec.IonMobilityValue, Is.Null);
            Assert.That(rec.IonMobilityType, Is.Null);
        }
    }
}
