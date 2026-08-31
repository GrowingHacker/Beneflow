/**
 * 主壳逻辑：动态加载页面片段 + 注册为 Vue 组件
 *
 * 页面片段格式（/pages/Xxx.html）：
 *   <div>...模板（单个根节点）...</div>
 *   <script page-setup>
 *   function pageSetup(deps) {
 *     const { ref, onMounted } = Vue;
 *     return { ... 响应式状态 / 方法 / 生命周期 ... };
 *   }
 *   </script>
 *
 * deps 包含：currentUser, api, common, goPage, toast, confirm
 */
async function loadPage(path) {
  const res = await fetch(path);
  if (!res.ok) throw new Error('加载页面失败: ' + path);
  const html = await res.text();
  const scriptMatch = html.match(/<script\s+page-setup[^>]*>([\s\S]*?)<\/script>/i);
  let pageSetup = function () { return {}; };
  if (scriptMatch) {
    // 执行脚本体以定义 pageSetup 函数
    pageSetup = new Function(scriptMatch[1] + '\n;return (typeof pageSetup !== "undefined") ? pageSetup : function(){return {};};')();
  }
  let template = html;
  if (scriptMatch) template = html.replace(scriptMatch[0], '').trim();
  return { template: template, pageSetup: pageSetup };
}

// 页面索引 -> 文件映射（与 index.html 菜单 index 一一对应）
const PAGE_FILES = [
  '/pages/Dashboard.html',     // 0
  '/pages/Sales.html',         // 1
  '/pages/SaleReturns.html',   // 2
  '/pages/Credits.html',       // 3
  '/pages/Products.html',      // 4
  '/pages/Suppliers.html',     // 5
  '/pages/Purchases.html',     // 6
  '/pages/PurchaseReturns.html',// 7
  '/pages/Inventory.html',     // 8
  '/pages/StockWarnings.html', // 9
  '/pages/Expiry.html',        // 10
  '/pages/StockCheck.html',    // 11
  '/pages/StockLog.html',      // 12
  '/pages/Reports.html',       // 13
  '/pages/Users.html',         // 14
  '/pages/Roles.html',         // 15
  '/pages/Menus.html',         // 16
  '/pages/Logs.html',          // 17
  '/pages/Settings.html',      // 18
];

const PAGE_TITLES = [
  '首页看板', '收银台', '销售退货', '赊账管理',
  '商品档案', '供应商管理', '进货单', '采购退货',
  '实时库存', '库存预警', '临期商品', '盘点单',
  '库存流水', '财务报表', '用户管理', '角色管理',
  '菜单管理', '操作日志', '系统设置',
];

// 每个页面对应的权限码（与后端登录返回的 user.permissions 对齐，"*" 表示全部）
const PAGE_PERMS = [
  'dashboard',     // 0
  'sales',         // 1
  'sale-returns',  // 2
  'credits',       // 3
  'products',      // 4
  'suppliers',     // 5
  'purchases',     // 6
  'purchase-returns', // 7
  'inventory',     // 8
  'stock-warnings',// 9
  'expiry',        // 10
  'stock-check',   // 11
  'stock-log',     // 12
  'reports',       // 13
  'users',         // 14
  'roles',         // 15
  'menus',         // 16
  'logs',          // 17
  'settings',      // 18
];

// 菜单结构配置（按权限过滤后渲染到侧边栏；子菜单整组无权限时隐藏）
const MENU_CONFIG = [
  { index: '0', label: '📊 首页看板', perm: 'dashboard' },
  { index: 'g-sales', label: '💰 销售管理', children: [
    { index: '1', label: '收银台', perm: 'sales' },
    { index: '2', label: '销售退货', perm: 'sale-returns' },
    { index: '3', label: '赊账管理', perm: 'credits' },
  ]},
  { index: 'g-goods', label: '📦 商品管理', children: [
    { index: '4', label: '商品档案', perm: 'products' },
    { index: '5', label: '供应商管理', perm: 'suppliers' },
  ]},
  { index: 'g-purchase', label: '📥 采购管理', children: [
    { index: '6', label: '进货单', perm: 'purchases' },
    { index: '7', label: '采购退货', perm: 'purchase-returns' },
  ]},
  { index: 'g-stock', label: '📋 库存管理', children: [
    { index: '8', label: '实时库存', perm: 'inventory' },
    { index: '9', label: '库存预警', perm: 'stock-warnings' },
    { index: '10', label: '临期商品', perm: 'expiry' },
    { index: '11', label: '盘点单', perm: 'stock-check' },
    { index: '12', label: '库存流水', perm: 'stock-log' },
  ]},
  { index: '13', label: '📈 财务报表', perm: 'reports' },
  { index: 'g-sys', label: '⚙️ 系统管理', children: [
    { index: '14', label: '用户管理', perm: 'users' },
    { index: '15', label: '角色管理', perm: 'roles' },
    { index: '16', label: '菜单管理', perm: 'menus' },
    { index: '17', label: '操作日志', perm: 'logs' },
    { index: '18', label: '系统设置', perm: 'settings' },
  ]},
];

async function initApp() {
  const { createApp, ref, provide, inject, onMounted, computed } = Vue;

  // 登录态校验
  const token = localStorage.getItem('token');
  const userJson = localStorage.getItem('user');
  if (!token || !userJson) { window.location.href = '/login.html'; return; }
  let currentUser = null;
  try { currentUser = JSON.parse(userJson); }
  catch (e) { window.location.href = '/login.html'; return; }

  // 并行加载所有页面片段
  let pages = [];
  try {
    pages = await Promise.all(PAGE_FILES.map(function (f) { return loadPage(f); }));
  } catch (e) {
    ElementPlus.ElMessage.error('页面加载失败：' + e.message);
    return;
  }

  const app = createApp({
    setup() {
      const ready = ref(false);
      const pwdDialog = ref(false);
      const pwdForm = ref({ oldPwd: '', newPwd: '', confirmPwd: '' });

      // 权限判断："*" 表示全部权限
      function can(perm) {
        const ps = (currentUser && currentUser.permissions) || [];
        return ps.indexOf('*') >= 0 || ps.indexOf(perm) >= 0;
      }

      // 按权限过滤菜单：无权限项剔除，子菜单整组无权限时整组隐藏
      const menus = Vue.computed(function () {
        return MENU_CONFIG.map(function (m) {
          if (m.children) {
            const kids = m.children.filter(function (c) { return can(c.perm); });
            return kids.length ? Object.assign({}, m, { children: kids }) : null;
          }
          return can(m.perm) ? m : null;
        }).filter(Boolean);
      });

      // 初始页校正：记忆的上次页面若无权限，回退到第一个有权限的页面
      function firstAllowed() {
        for (let i = 0; i < PAGE_PERMS.length; i++) { if (can(PAGE_PERMS[i])) return i; }
        return 0;
      }
      const saved = Number(localStorage.getItem('lastPage')) || 0;
      const page = ref(can(PAGE_PERMS[saved]) ? saved : firstAllowed());

      const pageTitle = Vue.computed(function () { return PAGE_TITLES[page.value] || ''; });

      function goPage(idx) {
        if (!(PAGE_PERMS[idx] && can(PAGE_PERMS[idx]))) {
          ElementPlus.ElMessage.warning('您没有访问该页面的权限');
          idx = firstAllowed();
          if (idx === page.value) return;
        }
        if (idx === page.value) return;
        page.value = idx;
        localStorage.setItem('lastPage', String(idx));
      }
      function onMenuSelect(index) { goPage(Number(index)); }

      function logout() {
        localStorage.removeItem('token');
        localStorage.removeItem('user');
        localStorage.removeItem('lastPage');
        window.location.href = '/login.html';
      }

      function onUserCommand(cmd) {
        if (cmd === 'logout') logout();
        else if (cmd === 'password') { pwdForm.value = { oldPwd: '', newPwd: '', confirmPwd: '' }; pwdDialog.value = true; }
      }

      const pwdSubmitting = ref(false);
      async function submitPwd() {
        const f = pwdForm.value;
        if (!f.oldPwd || !f.newPwd) { ElementPlus.ElMessage.warning('请填写完整'); return; }
        if (f.newPwd !== f.confirmPwd) { ElementPlus.ElMessage.error('两次新密码不一致'); return; }
        if (!/(?=.*[0-9])(?=.*[a-zA-Z]).{6,}/.test(f.newPwd)) {
          ElementPlus.ElMessage.error('新密码至少6位，且需同时包含数字和字母'); return;
        }
        pwdSubmitting.value = true;
        try {
          await window.api.post('/auth/change-password', { oldPassword: f.oldPwd, newPassword: f.newPwd });
          ElementPlus.ElMessage.success('密码修改成功，即将前往登录页');
          pwdDialog.value = false;
          setTimeout(logout, 1200); // 延迟跳转，留出时间展示成功提示
        } catch (e) {} // 失败提示由 api.js 响应拦截器统一弹出
        finally { pwdSubmitting.value = false; }
      }

      // 注入给各页面使用的公共依赖
      const deps = {
        currentUser: currentUser,
        api: window.api,
        common: window.common,
        goPage: goPage,
        toast: function (msg, type) { ElementPlus.ElMessage({ message: msg, type: type || 'info' }); },
        confirm: function (msg) {
          return ElementPlus.ElMessageBox.confirm(msg, '提示', {
            type: 'warning', confirmButtonText: '确认', cancelButtonText: '取消'
          });
        },
      };
      provide('deps', deps);

      onMounted(function () { ready.value = true; });

      return {
        ready: ready, page: page, pageTitle: pageTitle,
        menus: menus,
        currentUser: currentUser, onMenuSelect: onMenuSelect,
        logout: logout, onUserCommand: onUserCommand,
        pwdDialog: pwdDialog, pwdForm: pwdForm, submitPwd: submitPwd, pwdSubmitting: pwdSubmitting,
      };
    }
  });

  // 注册页面组件：每个页面拥有独立作用域与 onMounted（切到时挂载、切走时卸载）
  pages.forEach(function (p, i) {
    app.component('page-' + i, {
      template: p.template,
      setup: function () {
        const deps = inject('deps');
        return p.pageSetup(deps);
      }
    });
  });

  // 注册全部图标组件（Element Plus Icons），供页面片段模板直接使用，如 <Edit />
  if (window.ElementPlusIconsVue) {
    Object.keys(window.ElementPlusIconsVue).forEach(function (name) {
      app.component(name, window.ElementPlusIconsVue[name]);
    });
  }

  app.use(ElementPlus, {
    locale: window.ElementPlusLocaleZhCn // 中文语言包：日期面板/分页/空数据/消息框等组件文案
  });
  app.mount('#app');
}

window.onload = initApp;
