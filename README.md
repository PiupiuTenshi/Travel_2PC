# Track B — Student 1: TP Monitor / 2PC Coordinator

**English:** This .NET 10 project implements the Student 1 module: transaction primitives, Two-Phase Commit coordination, a durable recovery log, and Coordinator restart recovery. The local web page is a diagnostic tool. The included participants are test doubles, not the Student 2 database servers.

**Tiếng Việt:** Dự án .NET 10 này triển khai phần Student 1: các thao tác giao dịch, điều phối 2PC, nhật ký phục hồi bền vững và phục hồi Coordinator khi khởi động lại. Trang web cục bộ là công cụ kiểm tra. Hai participant đi kèm là mock phục vụ test, chưa phải máy chủ cơ sở dữ liệu của Student 2.

[Hướng dẫn toàn bộ mã nguồn, phân công và quy trình xây dựng / Full Vietnamese code and implementation guide](HUONG_DAN_DU_AN.md)

## 1. Ownership / Phân công

| Role / Vai trò | Responsibility / Trách nhiệm |
|---|---|
| **Student 1 — this module / mô-đun này** | BEGIN/END/ABORT, PREPARE votes, global COMMIT/ABORT, durable decision log and recovery / Các thao tác giao dịch, thu phiếu, quyết định toàn cục, log bền vững và phục hồi |
| Student 2 | Real Airline/Hotel Resource Managers: databases, locks, tentative writes, rollback and crash recovery / Resource Manager thật: database, khóa, ghi tạm, rollback và phục hồi sau crash |
| Student 3 | Booking RPC client, concurrent requests, idempotency tokens and chaos injector / Client RPC đặt chỗ, yêu cầu đồng thời, token chống trùng và công cụ gây lỗi |

**English:** The core Coordinator is implemented and can be verified with mocks. Full group integration still requires Student 2's real services and Student 3's booking client. The separate network fault injector, scenario runner, and participant administration panel have been removed to keep this submission focused on Student 1.

**Tiếng Việt:** Phần lõi Coordinator đã được triển khai và có thể kiểm chứng bằng mock. Việc tích hợp toàn nhóm vẫn cần dịch vụ thật của Student 2 và client đặt chỗ của Student 3. Bộ chèn lỗi mạng, trình chạy kịch bản và bảng quản trị participant đã được bỏ để bản nộp tập trung vào Student 1.

## 2. Run on Windows / Chạy trên Windows

**English:** Install the .NET 10 SDK, use PowerShell, and keep ports `5000`, `5101`, `5102` available. The first restore requires NuGet access. From the folder containing the solution:

**Tiếng Việt:** Cài .NET 10 SDK, dùng PowerShell và bảo đảm các cổng `5000`, `5101`, `5102` còn trống. Lần restore đầu cần kết nối NuGet. Tại thư mục chứa solution, chạy:

```powershell
dotnet --version
dotnet restore ./Travel2PC_Recovery_Demo.sln
dotnet build ./Travel2PC_Recovery_Demo.sln
```

**English:** Run each command below in its own terminal. Keep all three running:

**Tiếng Việt:** Mỗi lệnh dưới đây chạy trong một cửa sổ riêng. Giữ cả ba dịch vụ hoạt động:

```powershell
# Terminal 1 / Cửa sổ 1
./scripts/Run-Airline.ps1
```

```powershell
# Terminal 2 / Cửa sổ 2
./scripts/Run-Hotel.ps1
```

```powershell
# Terminal 3 / Cửa sổ 3
./scripts/Run-Coordinator.ps1
```

**English:** Open **http://localhost:5000** for the Coordinator diagnostic page, or `http://localhost:5000/health` for its health response. Restart previously running services after updating the source.

**Tiếng Việt:** Mở **http://localhost:5000** để dùng trang kiểm tra Coordinator hoặc `http://localhost:5000/health` để kiểm tra dịch vụ. Sau khi cập nhật mã, dừng và chạy lại các dịch vụ cũ.

### PowerShell script errors / Lỗi script PowerShell

**English:** If ZIP-extracted scripts report `is not digitally signed`, review them and remove the download marker only from the project scripts. This does not change the system execution policy:

**Tiếng Việt:** Nếu các script giải nén từ ZIP báo `is not digitally signed`, kiểm tra nội dung rồi gỡ dấu tải về riêng trong thư mục scripts. Lệnh này không thay đổi execution policy của máy:

```powershell
Get-ChildItem ./scripts -Filter '*.ps1' -File | Unblock-File
```

**English:** If policy still prevents scripts, run each service directly in a separate terminal:

**Tiếng Việt:** Nếu chính sách vẫn chặn script, chạy trực tiếp từng dịch vụ trong một cửa sổ riêng:

```powershell
# Terminal 1 / Cửa sổ 1
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project ./Travel.Participant.Mock -- --urls http://localhost:5101

# Terminal 2 / Cửa sổ 2
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project ./Travel.Participant.Mock -- --urls http://localhost:5102

# Terminal 3 / Cửa sổ 3
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project ./Travel.Coordinator -- --urls http://localhost:5000
```

## 3. Diagnostic page / Trang kiểm tra

**English:** The page provides BEGIN, END, ABORT, transaction lookup, ACK status, retry timing, a disk-backed logical log and the 30 most recent transactions. It uses plain HTML/CSS/JavaScript; no Node.js installation is needed. It is available only in Development.

**Tiếng Việt:** Trang có BEGIN, END, ABORT, tra cứu giao dịch, trạng thái ACK, lịch retry, nhật ký logic lưu trên đĩa và 30 giao dịch gần nhất. Trang dùng HTML/CSS/JavaScript thuần; không cần Node.js. Giao diện chỉ xuất hiện trong Development.

**English:** Three small Development helpers remain for Coordinator recovery checks: pause/resume decision notifications, force ACTIVE expiry, and write a simulated PREPARING state. These are test aids, not a chaos-injection module or a booking client.

**Tiếng Việt:** Giữ ba công cụ nhỏ trong Development để kiểm chứng phục hồi Coordinator: tạm dừng/tiếp tục gửi quyết định, ép ACTIVE hết hạn và ghi trạng thái PREPARING giả lập. Đây là công cụ hỗ trợ test, chưa phải mô-đun chaos injector hoặc client đặt chỗ.

## 4. Tests / Kiểm thử

**English:** With the three local services running and no other operator changing mock settings, run the four smoke scenarios: normal COMMIT, NO vote, ACTIVE expiry and lost COMMIT ACK:

**Tiếng Việt:** Khi ba dịch vụ local đang chạy và không có người khác đổi công tắc mock, kiểm tra bốn tình huống: COMMIT bình thường, phiếu NO, ACTIVE hết hạn và mất ACK của COMMIT:

```powershell
./scripts/Smoke-Test.ps1
```

**English:** After restore/build, the following script publishes an isolated copy, starts its own processes on ports `16000`–`16003`, uses a temporary database, and tests actual Coordinator process termination and restart. It stops only the processes it created. Logs and test data remain in the temporary directory printed by the script.

**Tiếng Việt:** Sau restore/build, script sau publish một bản riêng, tự khởi động các tiến trình ở cổng `16000`–`16003`, dùng database tạm và kiểm tra ngắt tiến trình Coordinator thật rồi phục hồi sau restart. Script chỉ dừng các tiến trình do chính nó tạo. Log và dữ liệu test được giữ tại thư mục tạm mà script in ra.

```powershell
./scripts/Test-Coordinator.ps1
# Optional alternate ports / Có thể chọn dải cổng khác:
# ./scripts/Test-Coordinator.ps1 -BasePort 17000
```

**English:** Test doubles expose minimal `/dev/vote`, `/dev/commit`, and `/dev/prepare-delay` controls for regression tests. They are not part of the integration contract with real Resource Managers.

**Tiếng Việt:** Mock cung cấp tối thiểu `/dev/vote`, `/dev/commit`, `/dev/prepare-delay` cho kiểm thử hồi quy. Các API này không thuộc hợp đồng tích hợp với Resource Manager thật.

## 5. API contract / Hợp đồng API

| Primitive / Thao tác | Coordinator HTTP API |
|---|---|
| `BEGIN_TRANSACTION` | `POST /api/transactions` |
| `END_TRANSACTION` | `POST /api/transactions/{id}/end` |
| `ABORT_TRANSACTION` | `POST /api/transactions/{id}/abort` |
| Read state / Đọc trạng thái | `GET /api/transactions/{id}` |
| Read recovery log / Đọc nhật ký | `GET /api/transactions/{id}/logs` |
| Health / Tình trạng dịch vụ | `GET /health` |

| 2PC message / Thông điệp | Resource Manager HTTP API |
|---|---|
| `VOTE_REQUEST` / PREPARE | `POST /api/2pc/prepare` |
| `GLOBAL_COMMIT` | `POST /api/2pc/commit` |
| `GLOBAL_ABORT` | `POST /api/2pc/abort` |

**English:** The request body sent to a participant is currently `{ "transactionId": "<GUID>" }`. Confirm the booking payload or staging procedure, response format and retry/idempotency rules with Students 2 and 3 before integration. This project does not currently implement BEGIN idempotency-token handling.

**Tiếng Việt:** Nội dung gửi đến participant hiện là `{ "transactionId": "<GUID>" }`. Trước khi tích hợp, thống nhất với Student 2/3 về dữ liệu đặt chỗ hoặc bước ghi tạm, định dạng phản hồi và luật retry/chống trùng. Hiện project chưa xử lý idempotency token cho BEGIN.

## 6. Recovery and durability / Phục hồi và độ bền

**English:** Transaction state and logical decision events are written in the same local SQLite transaction, before Phase 2 notifications. SQLite uses WAL and per-connection `synchronous=FULL`. The logical recovery log is the `TransactionLogs` table; it is distinct from SQLite's physical WAL files. Startup recovery reads persisted state in `Transactions`.

**Tiếng Việt:** Trạng thái giao dịch và sự kiện quyết định được ghi trong cùng một giao dịch SQLite cục bộ trước khi gửi thông báo pha 2. SQLite dùng WAL và `synchronous=FULL` trên mỗi kết nối. Nhật ký phục hồi logic nằm trong bảng `TransactionLogs`, khác với tệp WAL vật lý của SQLite. Phục hồi khởi động đọc trạng thái bền vững trong `Transactions`.

| Mechanism / Cơ chế | Behavior / Cách xử lý |
|---|---|
| Startup recovery / Phục hồi khởi động | Abandoned PREPARING and expired ACTIVE → ABORT_DECIDED / PREPARING bỏ dở và ACTIVE hết hạn được quyết định ABORT |
| RecoveryWorker | Retry due COMMIT_DECIDED/ABORT_DECIDED with missing ACKs / Thử lại quyết định đã đến hạn và còn thiếu ACK |
| TransactionCleanupWorker | Abort only expired ACTIVE transactions / Chỉ hủy ACTIVE đã hết hạn |

**English:** A durable COMMIT must never become ABORT. Retry backoff is 2, 4, 8, 16, then 30 seconds, with a default worker scan interval of 5 seconds. Do not delete or move `.db`, `.db-wal`, or `.db-shm` files while a service is using them.

**Tiếng Việt:** Quyết định COMMIT đã bền vững không được đổi thành ABORT. Khoảng chờ retry là 2, 4, 8, 16 rồi tối đa 30 giây; worker mặc định quét mỗi 5 giây. Không xóa hoặc di chuyển `.db`, `.db-wal`, `.db-shm` khi dịch vụ còn dùng các tệp này.

## 7. Docker Compose (optional) / Docker Compose (tùy chọn)

**English:** With Docker Engine running, use the following commands from the project root. Avoid running local services on the same ports simultaneously.

**Tiếng Việt:** Khi Docker Engine đã chạy, dùng các lệnh sau tại thư mục gốc. Không chạy đồng thời dịch vụ local chiếm cùng cổng.

```powershell
docker compose up --build -d
docker compose ps
docker compose logs -f coordinator
```

**English:** Restart only Coordinator to demonstrate recovery while keeping participant RAM intact:

**Tiếng Việt:** Chỉ khởi động lại Coordinator để demo phục hồi, giữ nguyên bộ nhớ của các participant:

```powershell
docker compose stop coordinator
docker compose start coordinator
```

**English:** `docker compose down` stops the demo and preserves its named database volume. Do not add `-v` if you need that data. Mock state is lost when a participant process/container restarts.

**Tiếng Việt:** `docker compose down` dừng demo và giữ named volume chứa database. Không thêm `-v` nếu cần giữ dữ liệu. Trạng thái mock bị mất khi tiến trình/container participant khởi động lại.

## 8. Completion and handoff / Hoàn thành và bàn giao

**English:** Student 1 should submit Coordinator source, the API/message contract, a state/sequence explanation, durable recovery evidence, and integration test results. The current mock-based tests establish Coordinator behavior; they do not prove atomic booking across Student 2's real databases.

**Tiếng Việt:** Student 1 cần bàn giao mã Coordinator, hợp đồng API/thông điệp, giải thích máy trạng thái/luồng giao tiếp, bằng chứng log bền vững và kết quả kiểm thử tích hợp. Các test với mock hiện chứng minh hành vi Coordinator, chưa chứng minh đặt chỗ nguyên tử qua hai database thật của Student 2.


