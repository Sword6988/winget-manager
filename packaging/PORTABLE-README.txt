Winget 软件管家 @VERSION@ —— 绿色（便携）版
============================================

整个文件夹就是全部，拷到哪都能跑：U 盘、桌面、D 盘、网盘同步目录都行。
不需要安装，也不需要管理员权限。

目录说明
--------
  WingetManager.exe     主程序，双击启动
  portable.tag          便携模式标记。别删 —— 删掉后程序会退回安装版行为，
                        把数据写回 %LOCALAPPDATA%，就不再是绿色版了
  data\                 首次启动自动生成：
                          selection.json       勾选记录
                          metadata-cache.json  包详情缓存
                          catalog-cache.json   列表快照（秒开用）
                          settings.json        窗口位置等设置
                          logs\app.log         运行日志
  其余文件              WinUI 3 运行时，别动

绿色到什么程度
--------------
  - 不写注册表（安装版会写 HKCU\Software\Shibeng\WingetManager）
  - 不写 %LOCALAPPDATA%
  - 不创建开始菜单 / 桌面快捷方式
  - 删除整个文件夹 = 零残留

和安装版的关系
--------------
  两者数据目录不同，互不影响：
    绿色版   <本文件夹>\data
    安装版   %LOCALAPPDATA%\WingetManager（设置另存注册表）
  想从安装版切过来，先在安装版里「导出清单」，再到绿色版「导入」。

迁移与多份配置
--------------
  搬走：整个文件夹拷贝走即可，勾选和缓存都跟着走。
  同一个 exe 想跑多套互不干扰的配置：设环境变量 WINGETMGR_DATA_DIR
  指向不同的数据目录（优先级高于 portable.tag）。

首次启动
--------
  检测到没有 catalog-cache.json 时会做一次全量扫描（取决于 winget 本身，
  通常十几秒）；之后启动走快照，秒开。
