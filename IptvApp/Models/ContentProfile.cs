namespace IptvApp.Models;

public enum ContentProfile
{
    Auto,        // Tự động detect từ metadata
    News,        // Tin tức / Talk show — ưu tiên sharpness nhẹ
    Sports,      // Thể thao — ưu tiên fps, không shader nặng
    Movie,       // Phim — chất lượng tối đa
    Anime,       // Hoạt hình — Anime4K
    Documentary  // Phim tài liệu — cân bằng
}
