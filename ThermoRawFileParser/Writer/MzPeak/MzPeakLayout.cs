namespace ThermoRawFileParser.Writer
{
    // Static layout constants for the mzPeak archive: the format/CV versions, the per-facet
    // spectrum/chromatogram array-index JSON blobs written to the parquet footer KV, and the
    // chrom-data CURIE set whose prefixes must reach cv_list. The emitted strings are unchanged.
    internal static class MzPeakLayout
    {
        public const string MzPeakVersion = "0.9";
        public const string MsCvVersion = "4.1.254";
        public const string UoCvVersion = "2026-01-16";

        // The nine chrom-data CURIEs whose prefixes must reach cv_list (finalized inside the metadata
        // facet). Registered via RegisterChromDataPrefixes before BuildMetadataFacet regardless of
        // whether chrom-data is streamed or buffered. Defined once in MzPeakCv.
        public static readonly string[] ChromDataAccessions = MzPeakCv.ChromDataAccessions;

        // Point spectra_data: the m/z and intensity point arrays carry their per-facet transform CURIEs
        // (MS:1003901 m/z, MS:1003902 intensity) with data_processing_id and sorting_rank, matching the
        // reference point spectra_data footer.
        public const string PointDataArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"point.mz\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"point\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"transform\":\"MS:1003902\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}" +
            "]}";

        // Point spectra_peaks: centroided peaks are stored verbatim, so both arrays carry a null
        // transform, matching the reference point spectra_peaks footer.
        public const string PointPeaksArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"point.mz\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"point\",\"transform\":null," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"transform\":null," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}" +
            "]}";

        public const string ChunkedSpectrumArrayIndex =
            "{\"prefix\":\"chunk\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_start\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_start\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_end\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_end\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_values\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_values\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.chunk_encoding\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_encoding\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"chunk_secondary\",\"transform\":\"MS:1003902\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}" +
            "]}";

        // m/z-only numpress layout: the four m/z anchor entries (MS:1003901) and the plain intensity
        // entry (chunk_secondary, MS:1003902) are unchanged; the m/z values live in
        // mz_numpress_linear_bytes (chunk_transform, MS:1002312, sorting_rank 0). 6 entries.
        public const string NumpressSpectrumArrayIndex =
            "{\"prefix\":\"chunk\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_start\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_start\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_end\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_end\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_values\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_values\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.chunk_encoding\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_encoding\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"chunk_secondary\",\"transform\":\"MS:1003902\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_numpress_linear_bytes\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_transform\",\"transform\":\"MS:1002312\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}" +
            "]}";

        public const string ChromatogramArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"chromatogram\",\"path\":\"point.time\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000595\"," +
            "\"array_name\":\"time array\",\"unit\":\"UO:0000031\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"chromatogram\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\"}," +
            "{\"context\":\"chromatogram\",\"path\":\"point.ms_level\",\"data_type\":\"MS:1000522\",\"array_type\":\"MS:1000786\"," +
            "\"array_name\":\"ms level\",\"unit\":\"UO:0000186\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\"}" +
            "]}";
    }
}
