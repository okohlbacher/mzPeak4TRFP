using System.Collections.Generic;
using System.Linq;

namespace ThermoRawFileParser.Writer
{
    // Accumulates the set of CV prefixes (the part before ':' in a CURIE) referenced anywhere in the
    // archive, and builds CV column names. Every accession or unit that reaches a column header or a
    // PARAM is routed through Collect/Cv, so OrderedPrefixes yields exactly the prefix set the archive
    // uses, sorted, for the generated cv_list. Owning this in a type makes the cv_list contract explicit
    // rather than an implicit side effect of name-building.
    internal sealed class CvCollector
    {
        private readonly HashSet<string> _prefixes = new HashSet<string>();

        // Records the CV prefix of a CURIE (no-op for null/empty or prefix-less strings).
        public void Collect(string curie)
        {
            if (string.IsNullOrEmpty(curie)) return;
            var i = curie.IndexOf(':');
            if (i > 0) _prefixes.Add(curie.Substring(0, i));
        }

        // Records the accession (and unit) prefixes and returns the snake_case CV column name.
        public string Cv(string accession, string label, string unit = null)
        {
            Collect(accession);
            if (unit != null) Collect(unit);
            return MzPeakParquet.CvColumn(accession, label, unit);
        }

        // The collected prefixes in ascending order, for cv_list emission.
        public IEnumerable<string> OrderedPrefixes() => _prefixes.OrderBy(p => p);
    }
}
