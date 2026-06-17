using MZPeak.ControlledVocabulary;

namespace MZPeak.Thermo
{

    public static class Instruments
    {
        /// <summary>
        /// Thermo instrument model string to CV mapping
        /// </summary>
        private static readonly Dictionary<string, Param> InstrumentModels =
            new Dictionary<string, Param>
            {
                {
                    "LTQ FT", new Param(
                        "LTQ FT",
                        "MS:1000448",
                        null
                    )
                },
                {
                    "LTQ FT ULTRA", new Param("LTQ FT Ultra", "MS:1000557", null)
                },
                {
                    "LTQ ORBITRAP", new Param("LTQ Orbitrap", "MS:1000449", null)
                },
                {
                    "LTQ ORBITRAP CLASSIC", new Param("LTQ Orbitrap Classic", "MS:1002835", null)
                },
                {
                    "LTQ ORBITRAP DISCOVERY", new Param("LTQ Orbitrap Discovery", "MS:1000555", null)
                },
                {
                    "LTQ ORBITRAP XL", new Param("LTQ Orbitrap XL", "MS:1000556", null)
                },
                {
                    "LTQ ORBITRAP XL ETD", new Param("LTQ Orbitrap XL ETD", "MS:1000639", null)
                },
                {
                    "MALDI LTQ ORBITRAP", new Param("MALDI LTQ Orbitrap", "MS:1000643", null)
                },
                {
                    "MALDI LTQ ORBITRAP XL", new Param("MALDI LTQ Orbitrap XL", "MS:1003496", null)
                },
                {
                    "MALDI LTQ ORBITRAP DISCOVERY", new Param("MALDI LTQ Orbitrap Discovery", "MS:1003497", null)
                },
                {
                    "LTQ ORBITRAP VELOS", new Param("LTQ Orbitrap Velos", "MS:1001742", null)
                },
                {
                    "LTQ ORBITRAP VELOS/ETD", new Param("LTQ Orbitrap Velos/ETD", "MS:1003499", null)
                },
                {
                    "ORBITRAP VELOS", new Param("LTQ Orbitrap Velos", "MS:1001742", null)
                },
                {
                    "LTQ ORBITRAP VELOS PRO", new Param("Orbitrap Velos Pro", "MS:1003096", null)
                },
                {
                    "ORBITRAP VELOS PRO", new Param("Orbitrap Velos Pro", "MS:1003096", null)
                },
                {
                    "LTQ ORBITRAP ELITE", new Param("Orbitrap Elite", "MS:1001910", null)
                },
                {
                    "ORBITRAP ELITE", new Param("Orbitrap Elite", "MS:1001910", null)
                },
                {
                    "LTQ", new Param("LTQ", "MS:1000447", null)
                },
                {
                    "LXQ", new Param("LXQ", "MS:1000450", null)
                },
                {
                    "LTQ XL", new Param("LTQ XL", "MS:1000854", null)
                },
                {
                    "LTQ XL ETD", new Param("LTQ XL ETD", "MS:1000638", null)
                },
                {
                    "MALDI LTQ XL", new Param("MALDI LTQ XL", "MS:1000642", null)
                },
                {
                    "LTQ VELOS", new Param("LTQ Velos", "MS:1000855", null)
                },
                {
                    "VELOS PRO", new Param("Velos Pro", "MS:1003495", null)
                },
                {
                    "LTQ VELOS ETD", new Param("LTQ Velos/ETD", "MS:1000856", null)
                },
                {
                    "ORBITRAP FUSION", new Param("Orbitrap Fusion", "MS:1002416", null)
                },
                {
                    "ORBITRAP FUSION ETD", new Param("Orbitrap Fusion ETD", "MS:1002417", null)
                },
                {
                    "ORBITRAP FUSION LUMOS", new Param("Orbitrap Fusion Lumos", "MS:1002732", null)
                },
                {
                    "ORBITRAP ECLIPSE", new Param("Orbitrap Eclipse", "MS:1003029", null)
                },
                {
                    "ORBITRAP ASCEND", new Param("Orbitrap Ascend", "MS:1003356", null)
                },
                {
                    "ORBITRAP EXPLORIS 120", new Param("Orbitrap Exploris 120", "MS:1003095", null)
                },
                                {
                    "ORBITRAP EXPLORIS 240", new Param("Orbitrap Exploris 240", "MS:1003094", null)
                },
                {
                    "ORBITRAP EXPLORIS 480", new Param("Orbitrap Exploris 480", "MS:1003028", null)
                },
                {
                    "ORBITRAP ASTRAL", new Param("Orbitrap Astral", "MS:1003378", null)
                },
                {
                    "EXACTIVE", new Param("Exactive", "MS:1000649", null)
                },
                {
                    "EXACTIVE PLUS", new Param("Exactive Plus", "MS:1002526", null)
                },
                {
                    "Q EXACTIVE", new Param("Q Exactive", "MS:1001911", null)
                },
                {
                    "Q EXACTIVE ORBITRAP", new Param("Q Exactive", "MS:1001911", null)
                },
                {
                    "Q EXACTIVE PLUS ORBITRAP", new Param("Q Exactive Plus", "MS:1002634", null)
                },
                {
                    "Q EXACTIVE HF", new Param("Q Exactive HF", "MS:1002523", null)
                },
                {
                    "Q EXACTIVE HF-X", new Param("Q Exactive HF-X", "MS:1002877", null)
                },
                {
                    "Q EXACTIVE PLUS", new Param("Q Exactive Plus", "MS:1002634", null)
                },
                {
                    "ORBITRAP ID-X", new Param("Orbitrap ID-X", "MS:1003112", null)
                },
                {
                    "TSQ QUANTUM ACCESS MAX", new Param("TSQ Quantum Access MAX", "MS:1003498", null)
                },
                {
                    "TSQ QUANTUM XLS", new Param("TSQ Quantum XLS", "MS:1003502", null)
                },
                {
                    "TSQ 8000", new Param("TSQ 8000", "MS:1003503", null)
                },
                {
                    "ISQ LT", new Param("ISQ LT", "MS:1003500", null)
                },
                {
                    "ITQ", new Param("ITQ", "MS:1003501", null)
                },
                {
                    "DELTAPLUS IRMS", new Param("DeltaPlus IRMS", "MS:1003504", null)
                },
                {
                    "THERMOQUEST VOYAGER", new Param("ThermoQuest Voyager", "MS:1003554", null)
                }
            };


        /// <summary>
        /// Get the instrument model CV param for the given instrument name
        /// </summary>
        /// <param name="instrumentName">the instrument name</param>
        /// <returns>the instrument CV param</returns>
        public static Param GetInstrumentModel(string instrumentName)
        {
            Param instrumentModel;
            instrumentName = instrumentName.ToUpper();
            if (InstrumentModels.ContainsKey(instrumentName))
            {
                instrumentModel = InstrumentModels[instrumentName];
            }
            else
            {
                var longestMatch = InstrumentModels.Where(pair => instrumentName.Contains(pair.Key))
                    .Select(pair => pair.Key)
                    .Aggregate("", (max, current) => max.Length > current.Length ? max : current);
                if (!(longestMatch == null || longestMatch.Length == 0))
                {
                    instrumentModel = InstrumentModels[longestMatch];
                }
                else
                {
                    instrumentModel = new Param("Thermo Fisher Scientific instrument model", "MS:1000483", null);
                }
            }

            return instrumentModel;
        }


        /// <summary>
        /// Get a list of detectors for the given instrument
        /// </summary>
        /// <param name="instrumentAccession">the instrument accession</param>
        /// <returns>a list of detectors</returns>
        public static List<Param> GetDetectors(string instrumentAccession)
        {
            List<Param> detectors;
            switch (instrumentAccession)
            {
                // LTQ FT
                case "MS:1000448":
                // LTQ FT ULTRA
                case "MS:1000557":
                // LTQ ORBITRAP
                case "MS:1000449":
                // LTQ ORBITRAP CLASSIC
                case "MS:1002835":
                // LTQ ORBITRAP DISCOVERY
                case "MS:1000555":
                // LTQ ORBITRAP XL
                case "MS:1000556":
                // LTQ ORBITRAP XL ETD
                case "MS:1000639":
                // MALDI LTQ ORBITRAP
                case "MS:1000643":
                // MALDI LTQ ORBITRAP XL
                case "MS:1003496":
                // MALDI LTQ ORBITRAP DISCOVERY
                case "MS:1003497":
                // LTQ ORBITRAP VELOS
                case "MS:1001742":
                // LTQ ORBITRAP VELOS/ETD
                case "MS:1003499":
                // LTQ ORBITRAP VELOS PRO
                case "MS:1003096":
                // LTQ ORBITRAP ELITE
                case "MS:1001910":
                // ORBITRAP FUSION
                case "MS:1002416":
                // ORBITRAP FUSION ETD
                case "MS:1002417":
                // ORBITRAP FUSION LUMOS
                case "MS:1002732":
                // ORBITRAP ECLIPSE
                case "MS:1003029":
                // ORBITRAP ASCEND
                case "MS:1003356":
                // ORBITRAP ID-X
                case "MS:1003112":
                    detectors = [
                        new Param("inductive detector", "MS:1000624", null),
                        new Param("electron multiplier", "MS:1000253", null),
                    ];
                    break;
                // ORBITRAP ASTRAL
                case "MS:1003378":
                    detectors = [
                        new Param("inductive detector", "MS:1000624", null),
                        new Param("conversion dynode electron multiplier", "MS:1000108", null),
                    ];
                    break;
                // EXACTIVE
                case "MS:1000649":
                // EXACTIVE PLUS
                case "MS:1002526":
                // Q EXACTIVE
                case "MS:1001911":
                // Q EXACTIVE HF
                case "MS:1002523":
                // Q EXACTIVE HF-X
                case "MS:1002877":
                // Q EXACTIVE PLUS
                case "MS:1002634":
                // ORBITRAP EXPLORIS 120
                case "MS:1003095":
                // ORBITRAP EXPLORIS 240
                case "MS:1003094":
                // ORBITRAP EXPLORIS 480
                case "MS:1003028":
                    detectors = [new Param("inductive detector", "MS:1000624", null),];
                    break;
                // LTQ
                case "MS:1000447":
                // LTQ VELOS
                case "MS:1000855":
                //  VELOS PRO
                case "MS:1003495":
                // LTQ VELOS ETD
                case "MS:1000856":
                // LXQ
                case "MS:1000450":
                // LTQ XL
                case "MS:1000854":
                // LTQ XL ETD
                case "MS:1000638":
                // MALDI LTQ XL
                case "MS:1000642":
                // TSQ QUANTUM ACCESS MAX
                case "MS:1003498":
                // TSQ QUANTUM XLS
                case "MS:1003502":
                // TSQ 8000
                case "MS:1003503":
                // ISQ LT
                case "MS:1003500":
                // ITQ
                case "MS:1003501":
                // THERMOQUEST VOYAGER
                case "MS:1003554":
                    detectors = [new Param("electron multiplier", "MS:1000253", null),];
                    break;
                // DELTAPLUS IRMS
                case "MS:1003504":
                    detectors = [new Param("faraday cup", "MS:1000112", null)];
                    break;
                default:
                    detectors = [new Param("inductive detector", "MS:1000624", null),];
                    break;
            }

            return detectors;
        }
    }
}