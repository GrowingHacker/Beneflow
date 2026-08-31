/**
 * 公共工具函数
 */
window.common = {
  // 金额格式化：123.4 -> "¥123.40"
  money: function (n) {
    const v = Number(n == null ? 0 : n);
    return '¥' + (isNaN(v) ? 0 : v).toFixed(2);
  },
  // 数字格式化（保留 2 位，无符号）
  num: function (n, digits) {
    const v = Number(n == null ? 0 : n);
    return (isNaN(v) ? 0 : v).toFixed(digits == null ? 2 : digits);
  },
  // 日期格式化 yyyy-MM-dd
  formatDate: function (d) {
    if (!d) return '';
    const dt = d instanceof Date ? d : new Date(d);
    if (isNaN(dt.getTime())) return '';
    const p = function (x) { return String(x).padStart(2, '0'); };
    return dt.getFullYear() + '-' + p(dt.getMonth() + 1) + '-' + p(dt.getDate());
  },
  // 日期时间格式化 yyyy-MM-dd HH:mm
  formatDateTime: function (d) {
    if (!d) return '';
    const dt = d instanceof Date ? d : new Date(d);
    if (isNaN(dt.getTime())) return '';
    const p = function (x) { return String(x).padStart(2, '0'); };
    return dt.getFullYear() + '-' + p(dt.getMonth() + 1) + '-' + p(dt.getDate())
      + ' ' + p(dt.getHours()) + ':' + p(dt.getMinutes());
  },
  // 今天往前 n 天的日期数组
  recentDays: function (n) {
    const arr = [];
    const today = new Date(); today.setHours(0, 0, 0, 0);
    for (let i = n - 1; i >= 0; i--) {
      const d = new Date(today); d.setDate(d.getDate() - i);
      arr.push(this.formatDate(d));
    }
    return arr;
  },
  // 生成单号前缀
  orderPrefix: function (p) {
    const d = new Date();
    const p2 = function (x) { return String(x).padStart(2, '0'); };
    return p + d.getFullYear() + p2(d.getMonth() + 1) + p2(d.getDate());
  },
  // 触发浏览器下载（Blob）
  downloadBlob: function (blob, filename) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  },
  // 权限判断：permissions 含 "*" 视为全部权限
  hasPerm: function (permissions, code) {
    if (!permissions || !permissions.length) return false;
    if (permissions.indexOf('*') >= 0) return true;
    return permissions.indexOf(code) >= 0;
  },
  /**
   * 归一化日期范围，保证 [起始日期, 截止日期] 顺序，避免前端 range[0]/range[1] 与
   * 后端 dateFrom/dateTo 语义不一致时查询结果为空或错位。
   * 参数可以是 ['YYYY-MM-DD', 'YYYY-MM-DD']（含空/undefined），也可以是其它长度或空值。
   * 返回 { from, to } 两个字符串（可能为空串），满足 from <= to（若两者都非空）。
   */
  normalizeDateRange: function (range) {
    let arr = [];
    if (Array.isArray(range)) {
      for (let i = 0; i < range.length; i++) arr.push(range[i]);
    }
    let from = arr.length < 1 || arr[0] == null ? '' : String(arr[0]).trim();
    let to   = arr.length < 2 || arr[1] == null ? '' : String(arr[1]).trim();
    if (from && to && from > to) { const t = from; from = to; to = t; }
    return { from: from, to: to };
  },
  /**
   * 两个独立日期字段（from/to，字符串 'YYYY-MM-DD'）的归一化。
   * 适用于把单个 daterange 拆成两个独立 date picker 后，语义仍然保持“起始 <= 截止”。
   * 返回 { from, to }，任一非法/空值会被规整为空串；若两端都非空且 from>to 自动互换。
   */
  normalizeDatePair: function (from, to) {
    let f = from == null ? '' : String(from).trim();
    let t = to   == null ? '' : String(to).trim();
    if (f && t && f > t) { const x = f; f = t; t = x; }
    return { from: f, to: t };
  }
};
