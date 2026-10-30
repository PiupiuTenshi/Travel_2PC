'use strict';

const $ = (id) => document.getElementById(id);
let selectedId = '';
let transaction = null;
let paused = false;
let busy = false;
let refreshing = false;

function element(tag, text, className = '') {
  const item = document.createElement(tag);
  if (text !== undefined) item.textContent = text;
  item.className = className;
  return item;
}
function notice(text, error = false) {
  $('notice').textContent = text;
  $('notice').className = error ? 'notice error' : 'notice';
}
async function request(path, method = 'GET') {
  const response = await fetch(path, { method, signal: AbortSignal.timeout(20000) });
  const text = await response.text();
  let data;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) throw new Error(data?.message || `HTTP ${response.status}. Kiểm tra dịch vụ và Transaction ID.`);
  return data;
}
const post = (path) => request(path, 'POST');
const time = (value) => value ? new Date(value).toLocaleTimeString('vi-VN', { hour12: false }) : '—';
function badge(status) {
  return `badge ${status === 'COMMITTED' ? 'good' : status === 'ABORTED' ? 'bad' : status?.includes('DECIDED') || status === 'PREPARING' ? 'pending' : ''}`;
}
function updateButtons() {
  document.querySelectorAll('button').forEach((button) => { button.disabled = busy; });
  $('end').disabled = busy || transaction?.status !== 'ACTIVE';
  $('abort').disabled = busy || !['ACTIVE', 'PREPARING', 'ABORT_DECIDED'].includes(transaction?.status);
  $('expire').disabled = busy || transaction?.status !== 'ACTIVE';
  $('preparing').disabled = busy || transaction?.status !== 'ACTIVE';
}
async function act(work) {
  if (busy) return;
  busy = true;
  updateButtons();
  try { await work(); } catch (error) { notice(error.message, true); }
  finally { busy = false; await refresh(); updateButtons(); }
}
function select(id) {
  selectedId = id;
  transaction = null;
  $('transaction-id').value = id;
  $('tx-status').textContent = 'ĐANG TẢI';
  $('tx-status').className = 'badge';
  for (const id of ['ack-airline', 'ack-hotel', 'retry-count', 'retry-at', 'expires-at']) $(id).textContent = '—';
  $('event-log').replaceChildren(element('li', 'Đang tải nhật ký…', 'empty'));
}
function renderTransaction(tx, logs) {
  transaction = tx;
  $('tx-status').textContent = tx.status;
  $('tx-status').className = badge(tx.status);
  $('ack-airline').textContent = tx.airlineAck ? 'Đã nhận' : 'Đang chờ';
  $('ack-hotel').textContent = tx.hotelAck ? 'Đã nhận' : 'Đang chờ';
  $('retry-count').textContent = tx.retryCount;
  $('retry-at').textContent = time(tx.nextRetryAtUtc);
  $('expires-at').textContent = time(tx.expiresAtUtc);
  const entries = logs.map((log) => {
    const row = element('li');
    const timestamp = element('time', time(log.createdAtUtc));
    timestamp.dateTime = log.createdAtUtc;
    const description = element('div');
    description.append(element('strong', log.eventType));
    if (log.details) description.append(element('p', log.details));
    row.append(timestamp, description);
    return row;
  });
  $('event-log').replaceChildren(...(entries.length ? entries : [element('li', 'Chưa có sự kiện.', 'empty')]));
}
function renderRecent(transactions) {
  const items = transactions.map((tx) => {
    const button = element('button', undefined, 'recent-tx');
    button.type = 'button';
    button.setAttribute('aria-pressed', String(tx.transactionId === selectedId));
    button.append(element('code', tx.transactionId), element('span', tx.status, badge(tx.status)));
    button.addEventListener('click', () => act(async () => select(tx.transactionId)));
    return button;
  });
  $('recent').replaceChildren(...(items.length ? items : [element('p', 'Chưa có giao dịch.', 'empty')]));
}
async function refresh() {
  if (refreshing) return;
  refreshing = true;
  const id = selectedId;
  try {
    const [state, recent] = await Promise.all([request('/dev/demo/state'), request('/dev/demo/transactions')]);
    paused = state.paused;
    $('pause').textContent = paused ? 'Tiếp tục gửi quyết định' : 'Tạm dừng gửi quyết định';
    $('timing-note').textContent = `HTTP timeout ${state.requestTimeoutSeconds} giây; ACTIVE TTL ${state.recovery.activeTtlSeconds} giây; retry quét mỗi ${state.recovery.retryScanSeconds} giây; cleanup mỗi ${state.recovery.cleanupScanSeconds} giây.`;
    renderRecent(recent);
    if (id) {
      const [tx, logs] = await Promise.all([request(`/api/transactions/${id}`), request(`/api/transactions/${id}/logs`)]);
      if (selectedId === id) renderTransaction(tx, logs);
    }
    $('connection').textContent = 'Coordinator đang hoạt động';
    $('connection').className = 'online';
  } catch (error) {
    $('connection').textContent = 'Không tải được dữ liệu';
    $('connection').className = 'offline';
    notice(`${error.message} Nếu Coordinator đang dừng, trang sẽ tự kết nối khi dịch vụ chạy lại.`, true);
  } finally { refreshing = false; updateButtons(); }
}
$('begin').addEventListener('click', () => act(async () => {
  const tx = await post('/api/transactions');
  select(tx.transactionId);
  transaction = tx;
  notice('Đã BEGIN. Chọn END để bắt đầu 2PC hoặc ABORT để hủy.');
}));
for (const action of ['end', 'abort']) {
  $(action).addEventListener('click', () => act(async () => {
    const result = await post(`/api/transactions/${selectedId}/${action}`);
    notice(`${action.toUpperCase()}: ${result.status}.`);
  }));
}
$('load').addEventListener('click', () => act(async () => {
  const id = $('transaction-id').value.trim();
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) throw new Error('Nhập Transaction ID dạng GUID hợp lệ.');
  await request(`/api/transactions/${id}`);
  select(id);
  notice('Đang theo dõi giao dịch đã chọn.');
}));
$('pause').addEventListener('click', () => act(async () => {
  await post(`/dev/notifications/${paused ? 'RESUME' : 'PAUSE'}`);
  notice(paused ? 'Đã tiếp tục gửi quyết định; worker sẽ retry theo lịch.' : 'Đã PAUSE: quyết định vẫn được lưu, nhưng chưa gửi COMMIT/ABORT.');
}));
$('expire').addEventListener('click', () => act(async () => {
  await post(`/dev/transactions/${selectedId}/expire`);
  notice('Đã ép ACTIVE hết hạn. Chờ CleanupWorker xử lý.');
}));
$('preparing').addEventListener('click', () => act(async () => {
  await post(`/dev/transactions/${selectedId}/simulate-preparing`);
  notice('Đã ghi PREPARING giả lập. Khởi động lại chỉ Coordinator và xem DECISION_ABORT_STARTUP; thao tác này chưa gửi PREPARE thật.');
}));
async function poll() { await refresh(); setTimeout(poll, 1500); }
poll();
