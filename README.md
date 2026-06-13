# Windows IPTV Player Native

Native IPTV Player dành riêng cho Windows được tối ưu hóa tối đa cho luồng trực tuyến tốc độ cao và độ trễ cực thấp (phù hợp tuyệt vời với các luồng truyền hình trực tiếp UDP/RTP Multicast của các nhà mạng). 

Dự án được xây dựng dựa trên giao diện người dùng hiện đại **WinUI 3 (Windows App SDK)** kết hợp cùng lõi giải mã mạnh mẽ **libmpv (MPV Player)** thông qua cơ chế Win32 Native Interop.

---

## 🚀 Tính Năng Nổi Bật

### 1. Tối ưu hóa đặc trị luồng UDP/RTP Multicast
* **SO_RCVBUF Nâng Cao:** Nâng kích thước bộ đệm socket hệ thống lên **8MB** (`recv_buffer_size=8388608`), ngăn chặn hiện tượng rớt gói tin (Packet Loss) do hiện tượng nghẽn hoặc trễ (Jitter) mạng trên Windows.
* **Low-Latency Profile:** Vô hiệu hóa bộ đệm demuxer (`cache=no`) và bật chế độ đồng bộ độ trễ video để đạt độ trễ gần như thời gian thực (Zero-latency / Real-time).
* **Bỏ qua lỗi PCR:** Thêm cờ bỏ qua đứt quãng trục thời gian (`ignore_pcr_discontinuity=1`) giúp luồng phát tiếp tục chạy mượt mà ngay cả khi nhà mạng reset timestamp của kênh phát.

### 2. Auto-Reconnect (Tự Động Kết Nối Lại)
* Hệ thống giám sát luồng phát thời gian thực thông qua trạng thái lõi (`core-idle` và `eof-reached`). 
* Nếu đường truyền bị ngắt (mất điện đầu thu, rớt mạng tạm thời), ứng dụng sẽ tự động thử kết nối lại sau mỗi **3 giây** cho đến khi khôi phục được luồng phát.

### 3. Bộ lọc hình ảnh AI (Upscaling Shaders)
* Tích hợp các bộ lọc Shader cao cấp trực tiếp vào GPU:
  * **AMD FidelityFX CAS (Contrast Adaptive Sharpening):** Làm sắc nét chi tiết hình ảnh mà không tạo ra quầng sáng giả.
  * **FSRCNNX (4K):** Sử dụng mạng nơ-ron nhân tạo nâng cấp độ phân giải luồng phát lên chuẩn 4K sắc nét.
* Nút chuyển đổi nhanh **3 trạng thái riêng biệt** trực quan trên giao diện: `OFF` | `CAS (Sharp)` | `FSRCNNX (4K)`.

### 4. Bảng thông số kỹ thuật chuyên sâu (Playback Stats)
Bảng thông số (`Ctrl + I` hoặc nút trên giao diện) cung cấp các thông tin chẩn đoán nâng cao:
* **Trạng thái luồng:** Badge hiển thị thời gian thực (Đang phát ổn định / Buffering / Đang kết nối lại...).
* **Khung hình (FPS):** Hiển thị song song FPS luồng gốc (Container) và FPS thực tế (Estimated VF).
* **Tần số quét (Display Hz):** Giám sát tần số quét thực tế của màn hình (60Hz, 144Hz...).
* **Active GPU:** Hiển thị chính xác GPU nào đang chịu trách nhiệm giải mã và render video.
* **Chi tiết Bitrate:** Tách biệt thông số bitrate Video (Mbps) và Audio (Kbps).
* **Độ trễ Live Edge:** Tính toán độ trễ chênh lệch (ms) so với luồng phát gốc của nhà mạng.
* **Thông số bổ sung:** Định dạng Pixel (nv12, d3d11...), âm thanh, và số lượng khung hình trễ (VO Delayed).

### 5. Native Fullscreen & Điều khiển ẩn thông minh
* Phóng to toàn màn hình thông qua phím tắt **F11**, click đúp vào video hoặc nút bấm.
* Phím **ESC** hỗ trợ thoát nhanh chế độ toàn màn hình.
* Khi ở chế độ toàn màn hình, nút thoát sẽ tự động ẩn đi sau **5 giây** không di chuyển chuột và tự động hiện lại ngay khi phát hiện chuyển động con trỏ.

---

## 🛠️ Kiến Trúc Dự Án

Dự án được cấu trúc theo mô hình MVVM (Model-View-ViewModel):
1. **`IptvApp` (WinUI 3 Project):** Chứa giao diện (XAML), ViewModels, và các Control tùy biến.
   * `MpvPlayer.cs`: Control tùy biến kế thừa `Grid`, tạo cửa sổ con Win32 (`CreateWindowEx` với cờ `WS_EX_NOPARENTNOTIFY` để chống nhấp nháy màn hình) và gắn handle đồ họa (`wid`) cho `libmpv`.
2. **`IptvApp.Core` (.NET Core Library):** 
   * Quản lý thực thể `Channel` (Kênh phát), nhóm kênh, trạng thái yêu thích.
   * `AppDbContext.cs`: Sử dụng Entity Framework Core SQLite để lưu trữ cơ sở dữ liệu kênh phát cục bộ.

---

## 💻 Cách Biên Dịch & Chạy Dự Án

### Yêu Cầu Hệ Thống
* Windows 10/11 (x64)
* .NET 10.0 SDK hoặc mới hơn
* Visual Studio 2022 / JetBrains Rider

### Cài đặt thư viện Native libmpv
Bạn cần tải phiên bản **libmpv-2.dll** (dành cho Windows x64) và đặt nó vào thư mục gốc của dự án hoặc thư mục đầu ra của bản build (`bin/Debug/net10.0-windows.../win-x64/`).

### Biên dịch dự án
Sử dụng dòng lệnh:
```bash
dotnet build
```

### Xuất bản file cài đặt độc lập (Self-Contained Exe)
Để đóng gói ứng dụng thành một file `.exe` duy nhất có thể chạy trên các máy tính khác mà không yêu cầu cài đặt sẵn .NET Runtime:
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
```
Sản phẩm đầu ra sẽ nằm tại thư mục:
`IptvApp/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/`

---

## ⌨️ Các Phím Tắt Tiện Ích

* **`F11`**: Bật/tắt toàn màn hình.
* **`ESC`**: Thoát toàn màn hình nhanh.
* **`Ctrl + I`**: Bật/tắt bảng thông số kỹ thuật (Stats Sidebar).
