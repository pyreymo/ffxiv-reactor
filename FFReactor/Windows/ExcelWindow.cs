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

namespace FFReactor.Windows;

public class ExcelWindow : Window, IDisposable
{
    private readonly IDataManager dataManager = Plugin.DataManager;
    private readonly List<Type> sheetTypes;

    private Type? selectedType;
    private string searchText = string.Empty;

    private readonly List<object> rowCache = [];
    private PropertyInfo[] allColumnProperties = [];

    private int currentColumnPage = 0;
    private const int MaxColumnsPerPage = 60;

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

    public override void Draw()
    {
        using var splitTable = ImRaii.Table("SplitLayout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable);
        if (!splitTable.Success) return;

        ImGui.TableSetupColumn("LeftColumn", ImGuiTableColumnFlags.WidthFixed, 220f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("RightColumn", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawLeftPanel();

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
        allColumnProperties = [];
        currentColumnPage = 0;

        try
        {
            var methods = typeof(IDataManager).GetMethods()
                .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethod)
                .ToList();

            if (methods.Count == 0) return;

            var method = methods.FirstOrDefault(m => m.GetParameters().Length == 0)
                      ?? methods.OrderBy(m => m.GetParameters().Length).First();

            var paramInfos = method.GetParameters();
            var genericMethod = method.MakeGenericMethod(type);

            var args = new object?[paramInfos.Length];
            for (var i = 0; i < paramInfos.Length; i++)
            {
                var p = paramInfos[i];
                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                }
                else if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null)
                {
                    args[i] = Activator.CreateInstance(p.ParameterType);
                }
                else
                {
                    args[i] = null;
                }
            }

            var result = genericMethod.Invoke(dataManager, args);

            if (result == null) return;

            if (result is IEnumerable sheet)
            {
                var enumerator = sheet.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current != null)
                    {
                        rowCache.Add(enumerator.Current);
                    }
                }
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            allColumnProperties = [.. props
                .Where(p => p.Name != "RowId" && p.Name != "SubRowId")
                .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType.IsEnum || p.PropertyType.Name.Contains("SeString"))];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load sheet data");
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

        if (rowCache.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "该表无数据。");
            return;
        }

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
                    var rowIdText = rowIdProp?.GetValue(row)?.ToString() ?? "-";
                    ImGui.TextUnformatted(rowIdText);
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.SetClipboardText(rowIdText);
                    }

                    foreach (var prop in visibleColumns)
                    {
                        ImGui.TableNextColumn();
                        try
                        {
                            var val = prop.GetValue(row);
                            var fullText = val?.ToString() ?? "null";
                            var displayText = fullText;

                            if (displayText.Length > 50) displayText = string.Concat(displayText.AsSpan(0, 50), "...");
                            ImGui.TextUnformatted(displayText);

                            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            {
                                ImGui.SetClipboardText(fullText);
                            }

                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip(fullText.Length > 50 ? fullText : "右键点击复制");
                            }
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
