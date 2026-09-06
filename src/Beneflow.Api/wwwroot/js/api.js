/**
 * Axios 统一封装
 * - baseURL: /api/v1
 * - 请求拦截器：自动携带 JWT
 * - 响应拦截器：拆解统一响应体 { code, message, data }，成功时直接返回 data
 */
(function () {
  const EP = window.ElementPlus || {};
  const instance = axios.create({
    baseURL: '/api/v1',
    timeout: 20000,
  });

  // 请求拦截器：注入 token
  instance.interceptors.request.use(
    function (cfg) {
      const token = localStorage.getItem('token');
      if (token) cfg.headers.Authorization = 'Bearer ' + token;
      return cfg;
    },
    function (e) { return Promise.reject(e); }
  );

  // 统一错误提示；config.silent 为 true 时不弹（用于试探性请求，如先按条码查再按名称查）
  function toastError(cfg, msg, type) {
    if (cfg && cfg.silent) return;
    (EP.ElMessage || function () {})({ message: msg, type: type || 'error' });
  }

  // 响应拦截器：统一拆包
  instance.interceptors.response.use(
    function (res) {
      const body = res.data;
      if (body && typeof body === 'object' && 'code' in body) {
        if (body.code === 0) return body.data;
        toastError(res.config, body.message || '请求失败');
        return Promise.reject(body);
      }
      return body;
    },
    function (err) {
      const status = err.response && err.response.status;
      if (status === 401) {
        localStorage.removeItem('token');
        localStorage.removeItem('user');
        var msg401 = (err.response && err.response.data && err.response.data.message) || '登录已过期，请重新登录';
        toastError(err.config, msg401, 'warning');
        setTimeout(function () { window.location.href = '/login.html'; }, 800);
        return Promise.reject(err);
      }
      const msg = (err.response && err.response.data && err.response.data.message) || err.message || '网络错误';
      toastError(err.config, msg);
      return Promise.reject(err);
    }
  );

  window.api = instance;
})();
