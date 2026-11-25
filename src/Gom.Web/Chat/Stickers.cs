using System.Collections.Frozen;

namespace Gom.Web.Chat;

/// <summary>
/// The stickers a message may be: still Noto Emoji pictures (Google, Apache License 2.0), named by their code points, kept
/// with the page under wwwroot/stickers. The page lists the same ones, in the same packs and order; a test keeps the two in step.
/// </summary>
public static class Stickers
{
    public static readonly FrozenSet<string> Allowed = new[]
    {
        // Cảm xúc
        "1f600", "1f604", "1f606", "1f605", "1f602", "1f923", "1f60a", "1f607", "1f609", "1f60d", "1f970", "1f618",
        "1f929", "1f60b", "1f61c", "1f92a", "1f917", "1f92d", "1f914", "1f644", "1f60f", "1f60c", "1f634", "1f971",
        "1f60e", "1f973", "1f97a", "1f622", "1f62d", "1f631", "1f633", "1f62c", "1f910", "1f621", "1f624", "1f92f",
        "1f974", "1f975", "1f976", "1f912",
        // Cử chỉ
        "1f44d", "1f44e", "1f44c", "270c", "1f91e", "1f91f", "1f919", "1f44f", "1f64c", "1f64f", "1f44b", "1f4aa",
        "1f91d", "1faf6",
        // Yêu thương
        "2764", "1f495", "1f496", "1f497", "1f493", "1f498", "1f49d", "1f494", "1f48c", "1f48b", "1f339", "1f490",
        // Động vật
        "1f415", "1f436", "1f408", "1f431", "1f43c", "1f430", "1f43b", "1f437", "1f414", "1f986", "1f41f", "1f422",
        "1f418", "1f98b",
        // Đồ ăn
        "1f35c", "1f35a", "1f356", "1f357", "1f354", "1f355", "1f370", "1f369", "1f36d", "1f9cb", "2615", "1f37b",
        "1f349", "1f96d", "1f34a",
        // Lễ hội
        "1f389", "1f38a", "1f388", "1f382", "1f381", "1f9e7", "1f9e8", "1f3ee", "1f386", "1f525", "1f31f", "2728",
        "1f3c6", "1f340", "1f4af",
        // Thời tiết
        "2600", "1f324", "26c5", "1f325", "1f327", "26c8", "1f329", "2744", "2603", "1f308", "2614", "1f32a",
        "1f319", "2b50", "1f30a", "1f338", "1f342", "1f321",
        // Du lịch
        "2708", "1f697", "1f3cd", "1f6f5", "1f68c", "1f686", "1f6a2", "1f6b2", "1f3d6", "1f3d4", "1f3d5", "1f5fa",
        "1f9f3", "1f4f8", "1f3e8", "1f5fc",
        // Thể thao
        "26bd", "1f3c0", "1f3f8", "1f3d3", "1f3be", "1f3d0", "1f3ca", "1f6b4", "1f3c3", "1f9d8", "1f3cb", "1f947",
        "1f3c5", "1f3ae", "1f3a3", "1f945",
        // Học tập
        "1f4da", "270f", "1f4dd", "1f393", "1f4bb", "1f4f1", "23f0", "1f4c5", "1f4bc", "1f4a1", "2705", "274c",
        "2753", "1f4a4", "1f4b0", "1f3e0",
        // Cờ
        "1f1fb_1f1f3", "1f3c1", "1f6a9", "1f38c", "1f3f4", "1f3f3", "1f3f3_200d_1f308", "1f3f3_200d_26a7",
        "1f3f4_200d_2620", "1f1e6_1f1e8", "1f1e6_1f1e9", "1f1e6_1f1ea", "1f1e6_1f1eb", "1f1e6_1f1ec", "1f1e6_1f1ee",
        "1f1e6_1f1f1", "1f1e6_1f1f2", "1f1e6_1f1f4", "1f1e6_1f1f6", "1f1e6_1f1f7", "1f1e6_1f1f8", "1f1e6_1f1f9",
        "1f1e6_1f1fa", "1f1e6_1f1fc", "1f1e6_1f1fd", "1f1e6_1f1ff", "1f1e7_1f1e6", "1f1e7_1f1e7", "1f1e7_1f1e9",
        "1f1e7_1f1ea", "1f1e7_1f1eb", "1f1e7_1f1ec", "1f1e7_1f1ed", "1f1e7_1f1ee", "1f1e7_1f1ef", "1f1e7_1f1f1",
        "1f1e7_1f1f2", "1f1e7_1f1f3", "1f1e7_1f1f4", "1f1e7_1f1f6", "1f1e7_1f1f7", "1f1e7_1f1f8", "1f1e7_1f1f9",
        "1f1e7_1f1fb", "1f1e7_1f1fc", "1f1e7_1f1fe", "1f1e7_1f1ff", "1f1e8_1f1e6", "1f1e8_1f1e8", "1f1e8_1f1e9",
        "1f1e8_1f1eb", "1f1e8_1f1ec", "1f1e8_1f1ed", "1f1e8_1f1ee", "1f1e8_1f1f0", "1f1e8_1f1f1", "1f1e8_1f1f2",
        "1f1e8_1f1f3", "1f1e8_1f1f4", "1f1e8_1f1f5", "1f1e8_1f1f7", "1f1e8_1f1fa", "1f1e8_1f1fb", "1f1e8_1f1fc",
        "1f1e8_1f1fd", "1f1e8_1f1fe", "1f1e8_1f1ff", "1f1e9_1f1ea", "1f1e9_1f1ec", "1f1e9_1f1ef", "1f1e9_1f1f0",
        "1f1e9_1f1f2", "1f1e9_1f1f4", "1f1e9_1f1ff", "1f1ea_1f1e6", "1f1ea_1f1e8", "1f1ea_1f1ea", "1f1ea_1f1ec",
        "1f1ea_1f1ed", "1f1ea_1f1f7", "1f1ea_1f1f8", "1f1ea_1f1f9", "1f1ea_1f1fa", "1f1eb_1f1ee", "1f1eb_1f1ef",
        "1f1eb_1f1f0", "1f1eb_1f1f2", "1f1eb_1f1f4", "1f1eb_1f1f7", "1f1ec_1f1e6", "1f1ec_1f1e7", "1f1ec_1f1e9",
        "1f1ec_1f1ea", "1f1ec_1f1eb", "1f1ec_1f1ec", "1f1ec_1f1ed", "1f1ec_1f1ee", "1f1ec_1f1f1", "1f1ec_1f1f2",
        "1f1ec_1f1f3", "1f1ec_1f1f5", "1f1ec_1f1f6", "1f1ec_1f1f7", "1f1ec_1f1f8", "1f1ec_1f1f9", "1f1ec_1f1fa",
        "1f1ec_1f1fc", "1f1ec_1f1fe", "1f1ed_1f1f0", "1f1ed_1f1f2", "1f1ed_1f1f3", "1f1ed_1f1f7", "1f1ed_1f1f9",
        "1f1ed_1f1fa", "1f1ee_1f1e8", "1f1ee_1f1e9", "1f1ee_1f1ea", "1f1ee_1f1f1", "1f1ee_1f1f2", "1f1ee_1f1f3",
        "1f1ee_1f1f4", "1f1ee_1f1f6", "1f1ee_1f1f7", "1f1ee_1f1f8", "1f1ee_1f1f9", "1f1ef_1f1ea", "1f1ef_1f1f2",
        "1f1ef_1f1f4", "1f1ef_1f1f5", "1f1f0_1f1ea", "1f1f0_1f1ec", "1f1f0_1f1ed", "1f1f0_1f1ee", "1f1f0_1f1f2",
        "1f1f0_1f1f3", "1f1f0_1f1f5", "1f1f0_1f1f7", "1f1f0_1f1fc", "1f1f0_1f1fe", "1f1f0_1f1ff", "1f1f1_1f1e6",
        "1f1f1_1f1e7", "1f1f1_1f1e8", "1f1f1_1f1ee", "1f1f1_1f1f0", "1f1f1_1f1f7", "1f1f1_1f1f8", "1f1f1_1f1f9",
        "1f1f1_1f1fa", "1f1f1_1f1fb", "1f1f1_1f1fe", "1f1f2_1f1e6", "1f1f2_1f1e8", "1f1f2_1f1e9", "1f1f2_1f1ea",
        "1f1f2_1f1eb", "1f1f2_1f1ec", "1f1f2_1f1ed", "1f1f2_1f1f0", "1f1f2_1f1f1", "1f1f2_1f1f2", "1f1f2_1f1f3",
        "1f1f2_1f1f4", "1f1f2_1f1f5", "1f1f2_1f1f6", "1f1f2_1f1f7", "1f1f2_1f1f8", "1f1f2_1f1f9", "1f1f2_1f1fa",
        "1f1f2_1f1fb", "1f1f2_1f1fc", "1f1f2_1f1fd", "1f1f2_1f1fe", "1f1f2_1f1ff", "1f1f3_1f1e6", "1f1f3_1f1e8",
        "1f1f3_1f1ea", "1f1f3_1f1eb", "1f1f3_1f1ec", "1f1f3_1f1ee", "1f1f3_1f1f1", "1f1f3_1f1f4", "1f1f3_1f1f5",
        "1f1f3_1f1f7", "1f1f3_1f1fa", "1f1f3_1f1ff", "1f1f4_1f1f2", "1f1f5_1f1e6", "1f1f5_1f1ea", "1f1f5_1f1eb",
        "1f1f5_1f1ec", "1f1f5_1f1ed", "1f1f5_1f1f0", "1f1f5_1f1f1", "1f1f5_1f1f2", "1f1f5_1f1f3", "1f1f5_1f1f7",
        "1f1f5_1f1f8", "1f1f5_1f1f9", "1f1f5_1f1fc", "1f1f5_1f1fe", "1f1f6_1f1e6", "1f1f7_1f1ea", "1f1f7_1f1f4",
        "1f1f7_1f1f8", "1f1f7_1f1fa", "1f1f7_1f1fc", "1f1f8_1f1e6", "1f1f8_1f1e7", "1f1f8_1f1e8", "1f1f8_1f1e9",
        "1f1f8_1f1ea", "1f1f8_1f1ec", "1f1f8_1f1ed", "1f1f8_1f1ee", "1f1f8_1f1ef", "1f1f8_1f1f0", "1f1f8_1f1f1",
        "1f1f8_1f1f2", "1f1f8_1f1f3", "1f1f8_1f1f4", "1f1f8_1f1f7", "1f1f8_1f1f8", "1f1f8_1f1f9", "1f1f8_1f1fb",
        "1f1f8_1f1fd", "1f1f8_1f1fe", "1f1f8_1f1ff", "1f1f9_1f1e6", "1f1f9_1f1e8", "1f1f9_1f1e9", "1f1f9_1f1eb",
        "1f1f9_1f1ec", "1f1f9_1f1ed", "1f1f9_1f1ef", "1f1f9_1f1f0", "1f1f9_1f1f1", "1f1f9_1f1f2", "1f1f9_1f1f3",
        "1f1f9_1f1f4", "1f1f9_1f1f7", "1f1f9_1f1f9", "1f1f9_1f1fb", "1f1f9_1f1fc", "1f1f9_1f1ff", "1f1fa_1f1e6",
        "1f1fa_1f1ec", "1f1fa_1f1f2", "1f1fa_1f1f3", "1f1fa_1f1f8", "1f1fa_1f1fe", "1f1fa_1f1ff", "1f1fb_1f1e6",
        "1f1fb_1f1e8", "1f1fb_1f1ea", "1f1fb_1f1ec", "1f1fb_1f1ee", "1f1fb_1f1fa", "1f1fc_1f1eb", "1f1fc_1f1f8",
        "1f1fd_1f1f0", "1f1fe_1f1ea", "1f1fe_1f1f9", "1f1ff_1f1e6", "1f1ff_1f1f2", "1f1ff_1f1fc",
        "1f3f4_e0067_e0062_e0065_e006e_e0067_e007f", "1f3f4_e0067_e0062_e0073_e0063_e0074_e007f",
        "1f3f4_e0067_e0062_e0077_e006c_e0073_e007f",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The column's size: the longest code is a subdivision flag (England: 41 characters), with room to spare.</summary>
    public const int MaxCodeLength = 48;
}
