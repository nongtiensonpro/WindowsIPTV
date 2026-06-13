using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IptvApp.Core.Models;

namespace IptvApp.Core.Services;

public class ChannelHealthChecker
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
        ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true // Bỏ qua lỗi SSL để tránh false dead
    });

    private static readonly Regex UdpRtpRegex = new(@"^(udp|rtp)://@?([^:/]+):(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<List<ChannelHealthResult>> CheckAllAsync(
        List<Channel> channels,
        int maxParallel = 8,
        int timeoutSeconds = 8,
        IProgress<ChannelScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ChannelHealthResult>();
        int total = channels.Count;
        int completed = 0;
        int alive = 0;
        int dead = 0;

        var semaphore = new SemaphoreSlim(maxParallel);
        var tasks = new List<Task>();

        var lockObj = new object();

        foreach (var channel in channels)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await semaphore.WaitAsync(cancellationToken);

            var task = Task.Run(async () =>
            {
                ChannelHealthResult? result = null;
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    // Báo cáo tiến trình đang quét kênh này
                    lock (lockObj)
                    {
                        progress?.Report(new ChannelScanProgress
                        {
                            Total = total,
                            Completed = completed,
                            Alive = alive,
                            Dead = dead,
                            CurrentChannelName = channel.Name,
                            LastResult = null
                        });
                    }

                    result = await CheckChannelAsync(channel, timeoutSeconds, cancellationToken);
                }
                catch (Exception ex)
                {
                    result = new ChannelHealthResult
                    {
                        Channel = channel,
                        Status = ChannelStatus.Dead,
                        ErrorMessage = ex.Message,
                        Quality = SignalQuality.None
                    };
                }
                finally
                {
                    semaphore.Release();

                    if (result != null)
                    {
                        lock (lockObj)
                        {
                            results.Add(result);
                            completed++;
                            if (result.Status == ChannelStatus.Alive)
                                alive++;
                            else if (result.Status == ChannelStatus.Dead || result.Status == ChannelStatus.Timeout)
                                dead++;

                            progress?.Report(new ChannelScanProgress
                            {
                                Total = total,
                                Completed = completed,
                                Alive = alive,
                                Dead = dead,
                                CurrentChannelName = channel.Name,
                                LastResult = result
                            });
                        }
                    }
                }
            }, cancellationToken);

            tasks.Add(task);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Bị hủy giữa chừng, trả về kết quả hiện tại
        }

        return results;
    }

    public async Task<ChannelHealthResult> CheckChannelAsync(
        Channel channel,
        int timeoutSeconds = 8,
        CancellationToken cancellationToken = default)
    {
        var url = channel.StreamUrl.Trim();

        // 1. Phân biệt UDP / RTP Multicast
        var udpMatch = UdpRtpRegex.Match(url);
        if (udpMatch.Success)
        {
            var host = udpMatch.Groups[2].Value;
            var portStr = udpMatch.Groups[3].Value;
            if (int.TryParse(portStr, out int port))
            {
                return await CheckUdpChannelAsync(channel, host, port, timeoutSeconds, cancellationToken);
            }
        }

        // 2. HTTP / HLS Stream
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Kiểm tra xem có phải là HLS playlist .m3u8 không
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return await CheckHlsChannelAsync(channel, url, timeoutSeconds, cancellationToken);
            }
            else
            {
                return await CheckHttpChannelAsync(channel, url, timeoutSeconds, cancellationToken);
            }
        }

        // Không hỗ trợ định dạng URL
        return new ChannelHealthResult
        {
            Channel = channel,
            Status = ChannelStatus.Dead,
            ErrorMessage = "Định dạng URL không được hỗ trợ",
            Quality = SignalQuality.None
        };
    }

    private async Task<ChannelHealthResult> CheckHttpChannelAsync(
        Channel channel,
        string url,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Dùng HttpCompletionOption.ResponseHeadersRead để không tải toàn bộ luồng video
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                return new ChannelHealthResult
                {
                    Channel = channel,
                    Status = ChannelStatus.Dead,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    ErrorMessage = $"HTTP {(int)response.StatusCode} - {response.ReasonPhrase}",
                    Quality = SignalQuality.None
                };
            }

            int responseTime = (int)stopwatch.ElapsedMilliseconds;
            var contentType = response.Content.Headers.ContentType?.MediaType;

            // Đọc một phần dữ liệu nhỏ (tối đa 50KB) để tính bitrate và kiểm tra magic byte
            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            
            byte[] buffer = new byte[8192];
            int totalBytesRead = 0;
            var readStopwatch = Stopwatch.StartNew();
            
            // Đọc dữ liệu trong tối đa 1.5 giây để đo bitrate
            while (readStopwatch.ElapsedMilliseconds < 1500 && totalBytesRead < 64 * 1024)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (bytesRead == 0)
                    break;

                totalBytesRead += bytesRead;
            }
            readStopwatch.Stop();

            if (totalBytesRead == 0)
            {
                return new ChannelHealthResult
                {
                    Channel = channel,
                    Status = ChannelStatus.Dead,
                    ResponseTimeMs = responseTime,
                    ContentType = contentType,
                    ErrorMessage = "Kết nối thành công nhưng không có dữ liệu trả về",
                    Quality = SignalQuality.None
                };
            }

            // Kiểm tra magic bytes của MPEG-TS (Sync byte là 0x47)
            // Lưu ý: Đối với một số luồng, byte 0x47 có thể xuất hiện định kỳ mỗi 188 bytes.
            // Chúng ta kiểm tra xem byte 0x47 có xuất hiện ở các vị trí đầu tiên hoặc trong 188 byte đầu tiên hay không.
            bool isMpegTs = false;
            for (int i = 0; i < Math.Min(totalBytesRead, 188); i++)
            {
                if (buffer[i] == 0x47)
                {
                    // Kiểm tra tiếp xem cách 188 byte có tiếp tục là 0x47 không nếu có đủ dữ liệu
                    if (i + 188 < totalBytesRead)
                    {
                        if (buffer[i + 188] == 0x47)
                        {
                            isMpegTs = true;
                            break;
                        }
                    }
                    else
                    {
                        isMpegTs = true; // Không đủ dữ liệu để kiểm tra gói tiếp theo nhưng coi như đúng
                        break;
                    }
                }
            }

            if (isMpegTs && string.IsNullOrEmpty(contentType))
            {
                contentType = "video/MP2T";
            }

            double elapsedSec = readStopwatch.Elapsed.TotalSeconds;
            if (elapsedSec <= 0) elapsedSec = 0.1;
            double bitrateKbps = (totalBytesRead * 8.0) / (elapsedSec * 1000.0);

            // Đánh giá chất lượng tín hiệu
            SignalQuality quality = SignalQuality.Poor;
            if (bitrateKbps >= 2000 && responseTime < 2000)
                quality = SignalQuality.Good;
            else if (bitrateKbps >= 500 && responseTime < 5000)
                quality = SignalQuality.Fair;

            return new ChannelHealthResult
            {
                Channel = channel,
                Status = ChannelStatus.Alive,
                ResponseTimeMs = responseTime,
                ReceivedBytes = totalBytesRead,
                EstimatedBitrateKbps = bitrateKbps,
                ContentType = contentType,
                Quality = quality
            };
        }
        catch (OperationCanceledException)
        {
            bool wasTimedOut = !cancellationToken.IsCancellationRequested;
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = wasTimedOut ? ChannelStatus.Timeout : ChannelStatus.Unknown,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorMessage = wasTimedOut ? "Hết thời gian phản hồi (Timeout)" : "Đã hủy yêu cầu",
                Quality = SignalQuality.None
            };
        }
        catch (Exception ex)
        {
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = ChannelStatus.Dead,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                Quality = SignalQuality.None
            };
        }
    }

    private async Task<ChannelHealthResult> CheckHlsChannelAsync(
        Channel channel,
        string url,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // 1. Tải playlist m3u8 chính
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new ChannelHealthResult
                {
                    Channel = channel,
                    Status = ChannelStatus.Dead,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    ErrorMessage = $"HLS Playlist HTTP {(int)response.StatusCode} - {response.ReasonPhrase}",
                    Quality = SignalQuality.None
                };
            }

            int responseTime = (int)stopwatch.ElapsedMilliseconds;
            string playlistContent = await response.Content.ReadAsStringAsync(cts.Token);
            
            if (string.IsNullOrWhiteSpace(playlistContent) || !playlistContent.Contains("#EXTM3U"))
            {
                return new ChannelHealthResult
                {
                    Channel = channel,
                    Status = ChannelStatus.Dead,
                    ResponseTimeMs = responseTime,
                    ErrorMessage = "Nội dung HLS Playlist không hợp lệ (thiếu #EXTM3U)",
                    Quality = SignalQuality.None
                };
            }

            // Phân tích các dòng trong playlist
            var lines = playlistContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            string? firstSegmentUrl = null;
            string? variantPlaylistUrl = null;
            
            var baseUri = new Uri(url);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF"))
                {
                    // Đây là Master Playlist, dòng tiếp theo thường là URL của Playlist con
                    if (i + 1 < lines.Length)
                    {
                        var nextLine = lines[i + 1].Trim();
                        if (!nextLine.StartsWith("#"))
                        {
                            variantPlaylistUrl = nextLine;
                            break;
                        }
                    }
                }
                else if (line.StartsWith("#EXTINF"))
                {
                    // Đây là Media Playlist, dòng tiếp theo thường là URL của Segment
                    if (i + 1 < lines.Length)
                    {
                        var nextLine = lines[i + 1].Trim();
                        if (!nextLine.StartsWith("#"))
                        {
                            firstSegmentUrl = nextLine;
                            break;
                        }
                    }
                }
            }

            // 2. Nếu là Master Playlist, tải Playlist con trước
            if (!string.IsNullOrEmpty(variantPlaylistUrl) && string.IsNullOrEmpty(firstSegmentUrl))
            {
                var absoluteVariantUrl = Uri.TryCreate(variantPlaylistUrl, UriKind.Absolute, out var absUri)
                    ? absUri.AbsoluteUri
                    : new Uri(baseUri, variantPlaylistUrl).AbsoluteUri;

                using var variantResponse = await _httpClient.GetAsync(absoluteVariantUrl, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (variantResponse.IsSuccessStatusCode)
                {
                    string variantContent = await variantResponse.Content.ReadAsStringAsync(cts.Token);
                    var vLines = variantContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    
                    var variantBaseUri = new Uri(absoluteVariantUrl);

                    for (int i = 0; i < vLines.Length; i++)
                    {
                        var line = vLines[i].Trim();
                        if (line.StartsWith("#EXTINF"))
                        {
                            if (i + 1 < vLines.Length)
                            {
                                var nextLine = vLines[i + 1].Trim();
                                if (!nextLine.StartsWith("#"))
                                {
                                    firstSegmentUrl = nextLine;
                                    baseUri = variantBaseUri; // Cập nhật baseUri để resolve segment
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 3. Nếu tìm thấy Segment, kiểm tra xem segment đó có tải được không
            if (!string.IsNullOrEmpty(firstSegmentUrl))
            {
                var absoluteSegmentUrl = Uri.TryCreate(firstSegmentUrl, UriKind.Absolute, out var absUri)
                    ? absUri.AbsoluteUri
                    : new Uri(baseUri, firstSegmentUrl).AbsoluteUri;

                var segmentStopwatch = Stopwatch.StartNew();
                // Đọc segment đầu tiên (chỉ cần lấy headers để xem sống hay không)
                using var segmentResponse = await _httpClient.GetAsync(absoluteSegmentUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                segmentStopwatch.Stop();

                if (segmentResponse.IsSuccessStatusCode)
                {
                    // Lấy kích thước segment nếu có
                    long? contentLength = segmentResponse.Content.Headers.ContentLength;
                    double bitrateKbps = 1500; // Mặc định cho HLS nếu không tính được

                    if (contentLength.HasValue && segmentStopwatch.Elapsed.TotalSeconds > 0)
                    {
                        // Đây chỉ là một segment nhỏ, nhưng có thể ước tính
                        bitrateKbps = (contentLength.Value * 8.0) / (segmentStopwatch.Elapsed.TotalSeconds * 1000.0);
                    }

                    SignalQuality quality = SignalQuality.Poor;
                    if (responseTime < 2000 && segmentStopwatch.ElapsedMilliseconds < 2000)
                        quality = SignalQuality.Good;
                    else if (responseTime < 5000 && segmentStopwatch.ElapsedMilliseconds < 5000)
                        quality = SignalQuality.Fair;

                    return new ChannelHealthResult
                    {
                        Channel = channel,
                        Status = ChannelStatus.Alive,
                        ResponseTimeMs = responseTime,
                        ReceivedBytes = contentLength ?? 0,
                        EstimatedBitrateKbps = bitrateKbps,
                        ContentType = segmentResponse.Content.Headers.ContentType?.MediaType ?? "video/MP2T",
                        Quality = quality
                    };
                }
                else
                {
                    return new ChannelHealthResult
                    {
                        Channel = channel,
                        Status = ChannelStatus.Dead,
                        ResponseTimeMs = responseTime,
                        ErrorMessage = $"Lỗi tải segment: HTTP {(int)segmentResponse.StatusCode}",
                        Quality = SignalQuality.None
                    };
                }
            }

            // Nếu không tìm thấy segment nào trong file .m3u8 nhưng file tải thành công
            // (Thường gặp với Playlist trống hoặc Live stream chưa bắt đầu)
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = ChannelStatus.Alive,
                ResponseTimeMs = responseTime,
                ErrorMessage = "Playlist HLS tải thành công nhưng không tìm thấy segment phân đoạn",
                Quality = SignalQuality.Fair
            };
        }
        catch (OperationCanceledException)
        {
            bool wasTimedOut = !cancellationToken.IsCancellationRequested;
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = wasTimedOut ? ChannelStatus.Timeout : ChannelStatus.Unknown,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorMessage = wasTimedOut ? "Hết thời gian tải HLS Playlist (Timeout)" : "Đã hủy yêu cầu",
                Quality = SignalQuality.None
            };
        }
        catch (Exception ex)
        {
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = ChannelStatus.Dead,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                Quality = SignalQuality.None
            };
        }
    }

    private async Task<ChannelHealthResult> CheckUdpChannelAsync(
        Channel channel,
        string host,
        int port,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        UdpClient? udpClient = null;

        try
        {
            // Parse multicast IP
            if (!IPAddress.TryParse(host, out var multicastAddress))
            {
                return new ChannelHealthResult
                {
                    Channel = channel,
                    Status = ChannelStatus.Dead,
                    ErrorMessage = $"IP Multicast không hợp lệ: {host}",
                    Quality = SignalQuality.None
                };
            }

            // Khởi tạo UdpClient
            udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            // Join Multicast Group
            udpClient.JoinMulticastGroup(multicastAddress);

            // Chờ nhận dữ liệu
            byte[]? data = null;
            long totalBytesRead = 0;
            var readStopwatch = new Stopwatch();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            // Chờ gói dữ liệu đầu tiên để xác định luồng bắt đầu hoạt động (ResponseTime)
            var receiveTask = udpClient.ReceiveAsync(cts.Token);
            stopwatch.Start();
            var completedTask = await Task.WhenAny(receiveTask.AsTask(), Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cts.Token));

            if (completedTask == receiveTask.AsTask())
            {
                var firstResult = await receiveTask;
                stopwatch.Stop();
                int responseTime = (int)stopwatch.ElapsedMilliseconds;

                data = firstResult.Buffer;
                totalBytesRead += data.Length;

                // Đo luồng dữ liệu tiếp tục trong 1 giây để tính bitrate
                readStopwatch.Start();
                while (readStopwatch.ElapsedMilliseconds < 1000 && !cts.Token.IsCancellationRequested)
                {
                    if (udpClient.Available > 0)
                    {
                        var nextResult = await udpClient.ReceiveAsync(cts.Token);
                        totalBytesRead += nextResult.Buffer.Length;
                    }
                    else
                    {
                        await Task.Delay(20, cts.Token);
                    }
                }
                readStopwatch.Stop();

                double elapsedSec = readStopwatch.Elapsed.TotalSeconds;
                if (elapsedSec <= 0) elapsedSec = 0.1;
                double bitrateKbps = (totalBytesRead * 8.0) / (elapsedSec * 1000.0);

                SignalQuality quality = SignalQuality.Poor;
                if (bitrateKbps >= 2000 && responseTime < 1000)
                    quality = SignalQuality.Good;
                else if (bitrateKbps >= 500 && responseTime < 3000)
                    quality = SignalQuality.Fair;

                return new ChannelHealthResult
                {
                    Channel = channel,
                    Status = ChannelStatus.Alive,
                    ResponseTimeMs = responseTime,
                    ReceivedBytes = totalBytesRead,
                    EstimatedBitrateKbps = bitrateKbps,
                    ContentType = "video/MP2T (UDP/Multicast)",
                    Quality = quality
                };
            }
            else
            {
                // Timeout, có khả năng cao là đang chạy ngoài mạng nội bộ của ISP (false positive nếu đánh dấu là Dead)
                return new ChannelHealthResult
                {
                    Channel = channel,
                    Status = ChannelStatus.Unknown,
                    ResponseTimeMs = timeoutSeconds * 1000,
                    ErrorMessage = "Không nhận được gói dữ liệu Multicast nào (Có thể do thiết bị nằm ngoài mạng nội bộ của ISP)",
                    Quality = SignalQuality.None
                };
            }
        }
        catch (OperationCanceledException)
        {
            // Bị cancel từ bên ngoài
            bool wasTimedOut = !cancellationToken.IsCancellationRequested;
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = wasTimedOut ? ChannelStatus.Unknown : ChannelStatus.Unknown, // Luôn coi UDP timeout là Unknown
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorMessage = wasTimedOut ? "Timeout: Không nhận được phản hồi từ mạng UDP" : "Yêu cầu bị hủy",
                Quality = SignalQuality.None
            };
        }
        catch (SocketException ex)
        {
            // Lỗi socket vật lý (ví dụ: cổng đang bận, network down) -> Dead
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = ChannelStatus.Dead,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorMessage = $"Lỗi kết nối Socket: {ex.Message} (Code: {ex.SocketErrorCode})",
                Quality = SignalQuality.None
            };
        }
        catch (Exception ex)
        {
            return new ChannelHealthResult
            {
                Channel = channel,
                Status = ChannelStatus.Dead,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                Quality = SignalQuality.None
            };
        }
        finally
        {
            if (udpClient != null)
            {
                try
                {
                    udpClient.DropMulticastGroup(IPAddress.Parse(host));
                }
                catch { }
                udpClient.Close();
                udpClient.Dispose();
            }
        }
    }
}
