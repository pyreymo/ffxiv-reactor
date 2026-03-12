using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Serilog;

namespace SamplePlugin.Windows;

public class ExcelWindow : Window, IDisposable
{
    private readonly IDataManager dataManager = Plugin.DataManager;
    private readonly List<Type> sheetTypes;

    private Type? selectedType;
    private string searchText = string.Empty;

    private readonly List<object> rowCache = [];
    private PropertyInfo[] allColumnProperties = [];

    // 列分页状态
    private int currentColumnPage = 0;
    private const int MaxColumnsPerPage = 60; // 预留几列给 RowId 和安全边界

    // 诊断日志集合
    private readonly List<string> diagnostics = [];

    public ExcelWindow(Plugin plugin)
        : base("技能选择器##ExcelWindow", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        var assembly = typeof(Lumina.Excel.Sheets.Action).Assembly;
        sheetTypes = [.. assembly.GetTypes()
            .Where(t => t.Namespace == "Lumina.Excel.Sheets" && t.IsValueType)
            .OrderBy(t => t.Name)];
    }

    public void Dispose() { }

    private void LogDiag(string message)
    {
        var log = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        diagnostics.Add(log);
        Log.Debug("[ExcelWindowDiag] " + message);
    }

    public override void Draw()
    {
        using var splitTable = ImRaii.Table("SplitLayout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable);
        if (!splitTable.Success) return;

        ImGui.TableSetupColumn("LeftColumn", ImGuiTableColumnFlags.WidthFixed, 220f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("RightColumn", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        // --- 左侧：表名列表 ---
        ImGui.TableNextColumn();
        DrawLeftPanel();

        // --- 右侧：数据矩阵与诊断面板 ---
        ImGui.TableNextColumn();
        DrawRightPanel();
    }

    private void DrawLeftPanel()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##searchBox", "搜索表名...", ref searchText, 50);
        ImGui.Spacing();

        using var child = ImRaii.Child("SheetListChild");
        if (!child.Success) return;

        var query = searchText.ToLowerInvariant();
        foreach (var type in sheetTypes)
        {
            if (!string.IsNullOrWhiteSpace(query) && !type.Name.Contains(query, StringComparison.InvariantCultureIgnoreCase))
                continue;

            if (ImGui.Selectable(type.Name, selectedType == type))
            {
                LoadSheetData(type);
            }
        }
    }

    private void LoadSheetData(Type type)
    {
        selectedType = type;
        rowCache.Clear();
        diagnostics.Clear();
        allColumnProperties = [];
        currentColumnPage = 0; // 切换表格时重置页码

        LogDiag($"--- 开始加载表格: {type.Name} ---");

        try
        {
            var methods = typeof(IDataManager).GetMethods()
                .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethod)
                .ToList();

            LogDiag($"在 IDataManager 中找到 {methods.Count} 个名为 GetExcelSheet 的泛型方法。");

            if (methods.Count == 0)
            {
                LogDiag("错误：未能找到任何 GetExcelSheet 方法！请检查 Dalamud 版本。");
                return;
            }

            // 优先选择无参的，如果没有，选参数最少的
            var method = methods.FirstOrDefault(m => m.GetParameters().Length == 0)
                      ?? methods.OrderBy(m => m.GetParameters().Length).First();

            var paramInfos = method.GetParameters();
            LogDiag($"选中方法签名，包含 {paramInfos.Length} 个参数。");

            var genericMethod = method.MakeGenericMethod(type);

            // 安全构造参数数组：防止非可空值类型被传入 null
            var args = new object?[paramInfos.Length];
            for (var i = 0; i < paramInfos.Length; i++)
            {
                var p = paramInfos[i];
                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                    LogDiag($"参数 [{i}] ({p.Name}): 使用默认值 -> {args[i] ?? "null"}");
                }
                else if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null)
                {
                    args[i] = Activator.CreateInstance(p.ParameterType);
                    LogDiag($"参数 [{i}] ({p.Name}): 是不可空值类型，实例化默认值 -> {args[i]}");
                }
                else
                {
                    args[i] = null;
                    LogDiag($"参数 [{i}] ({p.Name}): 传入 null");
                }
            }

            LogDiag("开始 Invoke 反射调用...");
            var result = genericMethod.Invoke(dataManager, args);

            if (result == null)
            {
                LogDiag("错误：调用成功，但返回的 Sheet 对象为 null！(可能是没找到这个表或语言不支持)");
                return;
            }

            LogDiag($"反射调用成功！返回类型: {result.GetType().Name}");

            if (result is IEnumerable sheet)
            {
                var enumerator = sheet.GetEnumerator();
                var count = 0;

                LogDiag("开始枚举数据...");
                // 加载整张表
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current != null)
                    {
                        rowCache.Add(enumerator.Current);
                        count++;
                    }
                }
                LogDiag($"枚举完成，成功抓取全部 {count} 行数据。");
            }
            else
            {
                LogDiag("错误：返回对象未实现 IEnumerable，无法进行枚举！");
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            LogDiag($"该类型共有 {props.Length} 个公共实例属性。");

            allColumnProperties = [.. props
                .Where(p => p.Name != "RowId" && p.Name != "SubRowId")
                .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType.IsEnum || p.PropertyType.Name.Contains("SeString"))];

            LogDiag($"属性过滤完毕，总列数: {allColumnProperties.Length} (每页最多显示 {MaxColumnsPerPage} 列)");
        }
        catch (Exception ex)
        {
            LogDiag($"【致命错误】加载过程中发生异常！");
            LogDiag($"类型: {ex.GetType().Name}");
            LogDiag($"信息: {ex.Message}");
            if (ex.InnerException != null)
            {
                LogDiag($"内部异常类型: {ex.InnerException.GetType().Name}");
                LogDiag($"内部异常信息: {ex.InnerException.Message}");
            }
        }
    }

    private void DrawRightPanel()
    {
        if (selectedType == null)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "请在左侧选择一个表格查看数据。");
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"当前表格: {selectedType.Name}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"(已加载全部 {rowCache.Count} 行数据)");

        // ========================== 诊断信息面板 ==========================
        if (ImGui.CollapsingHeader("加载诊断信息 (展开以查看详细日志)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            using var diagChild = ImRaii.Child("DiagLogs", new Vector2(0, ImGui.GetTextLineHeight() * 8), true);
            if (diagChild.Success)
            {
                foreach (var log in diagnostics)
                {
                    if (log.Contains("错误") || log.Contains("异常") || log.Contains("致命"))
                        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), log);
                    else
                        ImGui.TextWrapped(log);
                }
            }
        }
        ImGui.Separator();
        // =================================================================

        if (rowCache.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "该表无数据，请查看上方的诊断信息面板排查错误。");
            return;
        }

        // ========================== 列分页控制UI ==========================
        var totalPages = (int)Math.Ceiling((double)allColumnProperties.Length / MaxColumnsPerPage);
        if (totalPages > 1)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), $"列分页 ({currentColumnPage + 1}/{totalPages}):");
            ImGui.SameLine();

            ImGui.BeginDisabled(currentColumnPage == 0);
            if (ImGui.Button("◀ 上一页##ColPrev")) currentColumnPage--;
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(currentColumnPage == totalPages - 1);
            if (ImGui.Button("下一页 ▶##ColNext")) currentColumnPage++;
            ImGui.EndDisabled();

            ImGui.SameLine();
            var startCol = (currentColumnPage * MaxColumnsPerPage) + 1;
            var endCol = Math.Min((currentColumnPage + 1) * MaxColumnsPerPage, allColumnProperties.Length);
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"当前显示列: {startCol} - {endCol} / 共 {allColumnProperties.Length} 列");

            ImGui.Spacing();
        }

        var visibleColumns = allColumnProperties
            .Skip(currentColumnPage * MaxColumnsPerPage)
            .Take(MaxColumnsPerPage)
            .ToArray();

        using var table = ImRaii.Table("DataGrid", visibleColumns.Length + 1,
            ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        if (!table.Success) return;

        ImGui.TableSetupColumn("RowId", ImGuiTableColumnFlags.WidthFixed, 60f);
        foreach (var prop in visibleColumns)
        {
            ImGui.TableSetupColumn(prop.Name);
        }
        ImGui.TableHeadersRow();

        var rowIdProp = selectedType.GetProperty("RowId");

        unsafe
        {
            var clipper = new ImGuiListClipper();
            var clipperPtr = new ImGuiListClipperPtr(&clipper);
            clipperPtr.Begin(rowCache.Count);

            while (clipperPtr.Step())
            {
                for (var i = clipperPtr.DisplayStart; i < clipperPtr.DisplayEnd; i++)
                {
                    var row = rowCache[i];
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rowIdProp?.GetValue(row)?.ToString() ?? "-");

                    foreach (var prop in visibleColumns)
                    {
                        ImGui.TableNextColumn();
                        try
                        {
                            var val = prop.GetValue(row);
                            var text = val?.ToString() ?? "null";

                            if (text.Length > 50) text = string.Concat(text.AsSpan(0, 50), "...");
                            ImGui.TextUnformatted(text);
                        }
                        catch
                        {
                            ImGui.TextUnformatted("Err");
                        }
                    }
                }
            }
            clipperPtr.End();
        }
    }
}
