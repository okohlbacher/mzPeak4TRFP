using log4net;
using System.Reflection;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    public class MzPeakSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MzPeakSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
        }

        public override void Write(IRawDataPlus raw, int firstScanNumber, int lastScanNumber)
        {
            if (!raw.HasMsData)
            {
                throw new RawFileParserException("No MS data in RAW file, no output will be produced");
            }

            ConfigureWriter(".mzpeak");

            Writer.Flush();
            Writer.Close();
        }
    }
}
