using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using IptvApp.Core.Models;

namespace IptvApp.Core.Services;

public static class M3uExporter
{
    public static string Export(List<Channel> channels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var channel in channels)
        {
            var extinf = new StringBuilder("#EXTINF:-1");

            if (!string.IsNullOrEmpty(channel.LogoUrl))
            {
                extinf.Append($" tvg-logo=\"{channel.LogoUrl}\"");
            }

            if (!string.IsNullOrEmpty(channel.GroupName))
            {
                extinf.Append($" group-title=\"{channel.GroupName}\"");
            }

            extinf.Append($",{channel.Name}");
            sb.AppendLine(extinf.ToString());
            sb.AppendLine(channel.StreamUrl);
        }

        return sb.ToString();
    }

    public static async Task ExportToFileAsync(string filePath, List<Channel> channels)
    {
        var content = Export(channels);
        
        // Dùng mã hóa UTF-8 để đảm bảo hiển thị đúng tiếng Việt / ký tự Unicode
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
    }
}
