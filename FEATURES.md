# XboxControllerTester – Phân tích tính năng

## 1. Giao diện hiển thị
* Trang chính sử dụng một `CommandBar` với nút "Reset đếm" và một `Border` chứa `ScrollViewer` để trình bày lưới trạng thái điều khiển.【F:MainPage.xaml†L13-L37】
* Khi khởi tạo, mã dựng động lưới hai cột với tiêu đề trạng thái rung, thông tin pin và các hàng hiển thị cho từng nhóm nút/trigger, lưu lại `StackPanel` để cập nhật nhanh.【F:MainPage.xaml.cs†L768-L802】

## 2. Chu kỳ cập nhật & thu thập trạng thái
* Trang đăng ký cả `DispatcherTimer` 33 ms cho cập nhật UI và `ThreadPoolTimer` 16 ms cho vòng lặp đọc nhanh, đồng thời xử lý vòng đời trang để khởi động/dừng các timer cùng watcher tương ứng.【F:MainPage.xaml.cs†L30-L351】
* Vòng lặp nhanh lưu `GamepadReading`, đếm cạnh trigger, và chuẩn bị dữ liệu rung mượt bằng bộ lọc `SmoothChannel`.【F:MainPage.xaml.cs†L540-L628】
* Vòng lặp UI cập nhật trạng thái tất cả nút, tăng bộ đếm khi phát hiện cạnh lên, xử lý tổ hợp đổi chế độ rung, giữ `LB+RB+Menu` để reset, rồi vẽ lại từng ô hiển thị.【F:MainPage.xaml.cs†L632-L711】

## 3. Bộ đếm nút và chức năng reset
* Bộ đếm cho các nút số, trigger và nút đặc biệt được lưu trong từ điển, khởi tạo trong constructor, và có thể reset hoàn toàn qua nút giao diện hoặc giữ tổ hợp phím trên tay cầm.【F:MainPage.xaml.cs†L47-L69】【F:MainPage.xaml.cs†L256-L269】【F:MainPage.xaml.cs†L365-L378】【F:MainPage.xaml.cs†L688-L695】

## 4. Chế độ rung & cảnh báo vượt ngưỡng
* Ứng dụng hỗ trợ ba chế độ rung (Off/Motor/Trigger) và cho phép chuyển bằng tổ hợp `LB+RB+DPadUp/Down`, đồng thời cập nhật tiêu đề hiển thị chế độ và loại tay cầm hiện tại.【F:MainPage.xaml.cs†L37-L45】【F:MainPage.xaml.cs†L676-L687】【F:MainPage.xaml.cs†L845-L867】
* Các giá trị rung được làm mượt, áp dụng ngưỡng chết, so sánh thay đổi lớn trước khi gửi `GamepadVibration` để giảm spam, cũng như dừng khi thiết bị bị tháo.【F:MainPage.xaml.cs†L95-L124】【F:MainPage.xaml.cs†L570-L626】【F:MainPage.xaml.cs†L921-L931】
* Hệ thống "vượt ngưỡng" theo mốc 10 lần nhấn: khi một nút đạt mốc mới, ô hiển thị nháy vàng trong thời gian ngắn và hàng đợi rung cảnh báo bên trái/phải được kích hoạt với cường độ phụ thuộc mức mốc.【F:MainPage.xaml.cs†L226-L236】【F:MainPage.xaml.cs†L1032-L1094】

## 5. Nhận diện tay cầm & phương thức kết nối
* Ứng dụng dò tay cầm hiện tại, xác định kết nối USB/Bluetooth để đổi màu viền giao diện và cập nhật thông tin.【F:MainPage.xaml.cs†L380-L403】
* Hai `DeviceWatcher` HID và sự kiện `RawGameController` được dùng để phân biệt các dòng sản phẩm (Jelling vs Durham), áp dụng logic chống giật và thời gian ổn định trước khi cập nhật loại thiết bị hiển thị.【F:MainPage.xaml.cs†L407-L495】

## 6. Đọc nhanh nút Guide/Share qua socket cục bộ
* Ngoài API chuẩn, ứng dụng mở `StreamSocket` tới dịch vụ cục bộ (cổng 12345) để đọc trạng thái Guide/Share với timeout ngắn, giảm độ trễ khi đếm số lần nhấn; cơ chế tự đóng kết nối khi lỗi và thử lại sau.【F:MainPage.xaml.cs†L198-L201】【F:MainPage.xaml.cs†L934-L1001】

## 7. Theo dõi pin tay cầm
* Tiêu đề pin được cập nhật định kỳ (~1.2 s) và phân biệt USB (luôn đang sạc) so với không dây (trạng thái hoặc phần trăm nếu có), với màu sắc thay đổi theo mức pin.【F:MainPage.xaml.cs†L870-L899】
* Trình đọc pin nhúng trả về trạng thái sạc cơ bản dựa trên khả năng cung cấp của API `Gamepad`.【F:MainPage.xaml.cs†L1002-L1022】

## 8. Biện pháp trải nghiệm người dùng
* Thanh công cụ bị loại khỏi focus khi điều khiển bằng tay cầm để tránh nhảy UI, nhờ các handler `GettingFocus` tùy biến.【F:MainPage.xaml.cs†L284-L333】
* Khi không tìm thấy tay cầm hoặc bị tháo, giao diện chuyển về trạng thái Unknown và viền trở lại trong suốt.【F:MainPage.xaml.cs†L355-L358】【F:MainPage.xaml.cs†L526-L536】
