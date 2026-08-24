#!/usr/bin/env python3
"""Append localization rows referenced in code but missing from Localization.csv."""

from __future__ import annotations

import csv
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CSV_PATH = ROOT / "MainAPP" / "Resources" / "Localization.csv"
LANGUAGES = ("zh-CN", "en-US", "ja-JP", "pt-BR")

# zh-CN, en-US, ja-JP, pt-BR
TRANSLATIONS: dict[str, tuple[str, str, str, str]] = {
    "Nav_DataSourceMonitoring": ("数据源监控", "Data Source Monitor", "データソース監視", "Monitor de fontes de dados"),
    "Alarm_LocalizedNames": ("多语言名称", "Localized names", "多言語名称", "Nomes localizados"),
    "Alarm_NameEn": ("名称(EN)", "Name (EN)", "名前(EN)", "Nome (EN)"),
    "Alarm_NameJa": ("名称(JA)", "Name (JA)", "名前(JA)", "Nome (JA)"),
    "Alarm_NamePt": ("名称(PT)", "Name (PT)", "名前(PT)", "Nome (PT)"),
    "Csv_Alarm_FileName": ("{0}_报警_{1:yyyyMMdd_HHmmss}.csv", "{0}_alarms_{1:yyyyMMdd_HHmmss}.csv", "{0}_アラーム_{1:yyyyMMdd_HHmmss}.csv", "{0}_alarmes_{1:yyyyMMdd_HHmmss}.csv"),
    "Csv_Alarm_Name": ("名称", "Name", "名前", "Nome"),
    "Csv_Alarm_PlcAddress": ("PLC地址", "PlcAddress", "PLCアドレス", "Endereco PLC"),
    "Csv_Alarm_Level": ("级别", "Level", "レベル", "Nivel"),
    "Csv_Alarm_Description": ("描述", "Description", "説明", "Descricao"),
    "Csv_Alarm_NameEn": ("名称(EN)", "Name (EN)", "名前(EN)", "Nome (EN)"),
    "Csv_Alarm_NameJa": ("名称(JA)", "Name (JA)", "名前(JA)", "Nome (JA)"),
    "Csv_Alarm_NamePt": ("名称(PT)", "Name (PT)", "名前(PT)", "Nome (PT)"),
    "Csv_Defect_FileName": ("{0}_缺陷_{1:yyyyMMdd_HHmmss}.csv", "{0}_defects_{1:yyyyMMdd_HHmmss}.csv", "{0}_不良_{1:yyyyMMdd_HHmmss}.csv", "{0}_defeitos_{1:yyyyMMdd_HHmmss}.csv"),
    "Csv_Defect_Name": ("名称", "Name", "名前", "Nome"),
    "Csv_Defect_PlcAddress": ("PLC地址", "PlcAddress", "PLCアドレス", "Endereco PLC"),
    "Csv_Defect_Severity": ("严重度", "Severity", "重大度", "Gravidade"),
    "Csv_Defect_Category": ("类别", "Category", "カテゴリ", "Categoria"),
    "Csv_Defect_NameEn": ("名称(EN)", "Name (EN)", "名前(EN)", "Nome (EN)"),
    "Csv_Defect_NameJa": ("名称(JA)", "Name (JA)", "名前(JA)", "Nome (JA)"),
    "Csv_Defect_NamePt": ("名称(PT)", "Name (PT)", "名前(PT)", "Nome (PT)"),
    "Csv_CounterAlarm_FileName": ("{0}_计数报警_{1:yyyyMMdd_HHmmss}.csv", "{0}_counter_alarms_{1:yyyyMMdd_HHmmss}.csv", "{0}_カウンタアラーム_{1:yyyyMMdd_HHmmss}.csv", "{0}_alarmes_contador_{1:yyyyMMdd_HHmmss}.csv"),
    "Csv_CounterAlarm_Name": ("名称", "Name", "名前", "Nome"),
    "Csv_CounterAlarm_PlcAddress": ("PLC地址", "PlcAddress", "PLCアドレス", "Endereco PLC"),
    "Csv_CounterAlarm_MaxValue": ("阈值上限", "MaxValue", "上限値", "Valor maximo"),
    "Csv_CounterAlarm_Enabled": ("启用", "Enabled", "有効", "Ativado"),
    "Csv_CounterAlarm_Unit": ("单位", "Unit", "単位", "Unidade"),
    "Csv_CounterAlarm_Description": ("描述", "Description", "説明", "Descricao"),
    "Csv_CounterAlarm_NameEn": ("名称(EN)", "Name (EN)", "名前(EN)", "Nome (EN)"),
    "Csv_CounterAlarm_NameJa": ("名称(JA)", "Name (JA)", "名前(JA)", "Nome (JA)"),
    "Csv_CounterAlarm_NamePt": ("名称(PT)", "Name (PT)", "名前(PT)", "Nome (PT)"),
    "Csv_DataSource_FileName": ("{0}_数据源_{1:yyyyMMdd_HHmmss}.csv", "{0}_datasources_{1:yyyyMMdd_HHmmss}.csv", "{0}_データソース_{1:yyyyMMdd_HHmmss}.csv", "{0}_fontes_dados_{1:yyyyMMdd_HHmmss}.csv"),
    "Csv_DataSource_SourceName": ("SourceName", "SourceName", "SourceName", "SourceName"),
    "Csv_DataSource_SourceNameEn": ("SourceNameEn", "SourceNameEn", "SourceNameEn", "SourceNameEn"),
    "Csv_DataSource_SourceNameJa": ("SourceNameJa", "SourceNameJa", "SourceNameJa", "SourceNameJa"),
    "Csv_DataSource_SourceNamePt": ("SourceNamePt", "SourceNamePt", "SourceNamePt", "SourceNamePt"),
    "Csv_DataSource_SourceType": ("SourceType", "SourceType", "SourceType", "SourceType"),
    "Csv_DataSource_SourceEnabled": ("SourceEnabled", "SourceEnabled", "SourceEnabled", "SourceEnabled"),
    "Csv_DataSource_SourceDescription": ("SourceDescription", "SourceDescription", "SourceDescription", "SourceDescription"),
    "Csv_DataSource_TriggerAddress": ("TriggerAddress", "TriggerAddress", "TriggerAddress", "TriggerAddress"),
    "Csv_DataSource_TriggerValue": ("TriggerValue", "TriggerValue", "TriggerValue", "TriggerValue"),
    "Csv_DataSource_AckValue": ("AckValue", "AckValue", "AckValue", "AckValue"),
    "Csv_DataSource_ValueName": ("ValueName", "ValueName", "ValueName", "ValueName"),
    "Csv_DataSource_ValueNameEn": ("ValueNameEn", "ValueNameEn", "ValueNameEn", "ValueNameEn"),
    "Csv_DataSource_ValueNameJa": ("ValueNameJa", "ValueNameJa", "ValueNameJa", "ValueNameJa"),
    "Csv_DataSource_ValueNamePt": ("ValueNamePt", "ValueNamePt", "ValueNamePt", "ValueNamePt"),
    "Csv_DataSource_ValueDataType": ("ValueDataType", "ValueDataType", "ValueDataType", "ValueDataType"),
    "Csv_DataSource_ValuePlcAddress": ("ValuePlcAddress", "ValuePlcAddress", "ValuePlcAddress", "ValuePlcAddress"),
    "Csv_DataSource_ValueUnit": ("ValueUnit", "ValueUnit", "ValueUnit", "ValueUnit"),
    "Csv_DataSource_ValueEnabled": ("ValueEnabled", "ValueEnabled", "ValueEnabled", "ValueEnabled"),
    "Csv_DataSource_LimitMin": ("LimitMin", "LimitMin", "LimitMin", "LimitMin"),
    "Csv_DataSource_LimitMax": ("LimitMax", "LimitMax", "LimitMax", "LimitMax"),
    "Csv_DataSource_FloatLimitMin": ("FloatLimitMin", "FloatLimitMin", "FloatLimitMin", "FloatLimitMin"),
    "Csv_DataSource_FloatLimitMax": ("FloatLimitMax", "FloatLimitMax", "FloatLimitMax", "FloatLimitMax"),
    "Csv_DataSource_Hysteresis": ("Hysteresis", "Hysteresis", "Hysteresis", "Hysteresis"),
    "Csv_DataSource_ConfirmSeconds": ("ConfirmSeconds", "ConfirmSeconds", "ConfirmSeconds", "ConfirmSeconds"),
    "Csv_DataSource_ExpectedValueConfigured": ("ExpectedValueConfigured", "ExpectedValueConfigured", "ExpectedValueConfigured", "ExpectedValueConfigured"),
    "Csv_DataSource_ExpectedValue": ("ExpectedValue", "ExpectedValue", "ExpectedValue", "ExpectedValue"),
    "Csv_DataSource_FloatExpectedValue": ("FloatExpectedValue", "FloatExpectedValue", "FloatExpectedValue", "FloatExpectedValue"),
    "Csv_DataSource_BoolExpectedValue": ("BoolExpectedValue", "BoolExpectedValue", "BoolExpectedValue", "BoolExpectedValue"),
    "Csv_DataSource_StringExpectedValue": ("StringExpectedValue", "StringExpectedValue", "StringExpectedValue", "StringExpectedValue"),
    "Csv_DataSource_StringLength": ("StringLength", "StringLength", "StringLength", "StringLength"),
    "Csv_DataSource_EnumValuesJson": ("EnumValuesJson", "EnumValuesJson", "EnumValuesJson", "EnumValuesJson"),
    "Dsm_Title": ("数据源监控", "Data Source Monitor", "データソース監視", "Monitor de fontes de dados"),
    "Dsm_Subtitle": ("实时查看各设备数据源采样值、状态与趋势", "Live view of sampled values, status, and trends", "各設備のデータソースサンプル値・状態・トレンドをリアルタイム表示", "Visualize valores amostrados, status e tendencias em tempo real"),
    "Dsm_DisplayMode": ("显示模式", "Display mode", "表示モード", "Modo de exibicao"),
    "Dsm_TableView": ("表格", "Table", "テーブル", "Tabela"),
    "Dsm_CompactCards": ("紧凑卡片", "Compact cards", "コンパクトカード", "Cartoes compactos"),
    "Dsm_TrendView": ("趋势图", "Trend", "トレンド", "Tendencia"),
    "Dsm_LastRefresh": ("上次刷新：", "Last refresh:", "最終更新:", "Ultima atualizacao:"),
    "Dsm_Refresh": ("刷新", "Refresh", "更新", "Atualizar"),
    "Dsm_TotalValues": ("总值项", "Total values", "値項目合計", "Total de valores"),
    "Dsm_AlarmValues": ("报警", "Alarm", "アラーム", "Alarme"),
    "Dsm_NormalValues": ("正常", "Normal", "正常", "Normal"),
    "Dsm_ExceptionsView": ("异常视图", "Exceptions view", "例外ビュー", "Visao de excecoes"),
    "Dsm_ExceptionHint": ("仅显示采样异常的值项", "Show only values with sampling exceptions", "サンプリング例外の値項目のみ表示", "Mostrar apenas valores com excecoes de amostragem"),
    "Dsm_DeviceFilter": ("设备筛选", "Device filter", "設備フィルター", "Filtro de dispositivo"),
    "Dsm_StateFilter": ("状态筛选", "State filter", "状態フィルター", "Filtro de estado"),
    "Dsm_Search": ("搜索名称/地址", "Search name/address", "名称/アドレス検索", "Pesquisar nome/endereco"),
    "Dsm_ClearFilters": ("清除筛选", "Clear filters", "フィルター解除", "Limpar filtros"),
    "Dsm_Device": ("设备", "Device", "設備", "Dispositivo"),
    "Dsm_Source": ("数据源", "Source", "データソース", "Fonte"),
    "Dsm_Value": ("值项", "Value", "値項目", "Valor"),
    "Dsm_Type": ("类型", "Type", "タイプ", "Tipo"),
    "Dsm_CurrentValue": ("当前值", "Current value", "現在値", "Valor atual"),
    "Dsm_Unit": ("单位", "Unit", "単位", "Unidade"),
    "Dsm_Address": ("地址", "Address", "アドレス", "Endereco"),
    "Dsm_Status": ("状态", "Status", "状態", "Status"),
    "Dsm_LastUpdated": ("更新时间", "Last updated", "更新時刻", "Atualizado em"),
    "Dsm_TrendDataGap": ("趋势中存在未采样点", "Trend contains unsampled gaps", "トレンドに未サンプル区間があります", "A tendencia contem lacunas sem amostra"),
    "Dsm_Details": ("详情", "Details", "詳細", "Detalhes"),
    "Dsm_LastValidValue": ("上次有效值", "Last valid value", "最終有効値", "Ultimo valor valido"),
    "Dsm_Freshness": ("新鲜度", "Freshness", "鮮度", "Atualidade"),
    "Dsm_Criteria": ("判定条件", "Criteria", "判定条件", "Criterios"),
    "Dsm_TriggerMode": ("采集模式", "Acquisition mode", "収集モード", "Modo de aquisicao"),
    "Dsm_TriggerAddress": ("触发地址", "Trigger address", "トリガーアドレス", "Endereco de gatilho"),
    "Dsm_TriggerValue": ("触发值", "Trigger value", "トリガー値", "Valor de gatilho"),
    "Dsm_AckValue": ("确认值", "Ack value", "確認値", "Valor de confirmacao"),
    "Dsm_SelectRow": ("选择一行以查看详情", "Select a row to view details", "行を選択して詳細を表示", "Selecione uma linha para ver detalhes"),
    "Dsm_Invalid": ("无效", "Invalid", "無効", "Invalido"),
    "Dsm_Int32": ("整数", "Int32", "整数", "Int32"),
    "Dsm_Float32": ("浮点", "Float32", "浮動小数", "Float32"),
    "Dsm_Bool": ("布尔", "Bool", "ブール", "Bool"),
    "Dsm_String": ("字符串", "String", "文字列", "String"),
    "Dsm_BoolTrue": ("是", "True", "はい", "Verdadeiro"),
    "Dsm_BoolFalse": ("否", "False", "いいえ", "Falso"),
    "Dsm_NotSampled": ("未采样", "Not sampled", "未サンプル", "Nao amostrado"),
    "Dsm_ReadFailed": ("读取失败", "Read failed", "読取失敗", "Falha na leitura"),
    "Dsm_Stale": ("过期", "Stale", "古い", "Desatualizado"),
    "Dsm_Fresh": ("新鲜", "Fresh", "最新", "Atual"),
    "Dsm_TimedSuffix": ("（定时采集）", " (Periodic)", "（定期収集）", " (Periodico)"),
    "Dsm_Periodic": ("定时采集", "Periodic", "定期収集", "Periodico"),
    "Dsm_Triggered": ("触发采集", "Triggered", "トリガー収集", "Por gatilho"),
    "Dsm_TrendSessionHint": ("最近 {0} 分钟，最多 {1} 个点", "Last {0} minutes, up to {1} points", "直近 {0} 分、最大 {1} 点", "Ultimos {0} minutos, ate {1} pontos"),
    "Dsm_TrendSelectValue": ("选择值项查看趋势", "Select a value to view trend", "値項目を選択してトレンド表示", "Selecione um valor para ver a tendencia"),
    "Dsm_TrendUnsupported": ("该类型不支持趋势", "Trend not supported for this type", "このタイプはトレンド非対応", "Tendencia nao suportada para este tipo"),
    "Dsm_TrendNoData": ("暂无趋势数据", "No trend data", "トレンドデータなし", "Sem dados de tendencia"),
    "Dsm_TrendSelectHint": ("在表格或卡片中选择值项", "Select a value in the table or cards", "テーブルまたはカードで値項目を選択", "Selecione um valor na tabela ou nos cartoes"),
    "Dsm_TrendNoDataHint": ("等待采样数据写入趋势缓存", "Waiting for sampled data in trend cache", "サンプルデータがトレンドキャッシュに入るのを待機", "Aguardando dados amostrados no cache de tendencia"),
    "Dsm_ShowingSummary": ("显示 {0}/{1}", "Showing {0}/{1}", "{0}/{1} を表示", "Exibindo {0}/{1}"),
    "Dsm_ExceptionSummary": ("异常 {0}", "Exceptions {0}", "例外 {0}", "Excecoes {0}"),
    "Dsm_NoMatch": ("无匹配结果", "No matches", "一致なし", "Sem correspondencias"),
    "Dsm_Empty": ("暂无数据源值项", "No configured values", "設定済み値項目なし", "Nenhum valor configurado"),
    "Dsm_NoMatchHint": ("尝试调整筛选或搜索条件", "Try adjusting filters or search", "フィルターまたは検索条件を調整してください", "Ajuste os filtros ou a pesquisa"),
    "Dsm_EmptyHint": ("请先在设备管理中配置数据源", "Configure data sources in Device Manager first", "まず設備管理でデータソースを設定してください", "Configure fontes de dados no Gerenciador de dispositivos"),
    "Dsm_AllStates": ("全部状态", "All states", "すべての状態", "Todos os estados"),
    "Dsm_Normal": ("正常", "Normal", "正常", "Normal"),
    "Dsm_Alarm": ("报警", "Alarm", "アラーム", "Alarme"),
    "Dsm_Exceptions": ("异常", "Exceptions", "例外", "Excecoes"),
    "Dsm_AllDevices": ("全部设备", "All devices", "すべての設備", "Todos os dispositivos"),
    "Dsm_Unspecified": ("未指定", "Unspecified", "未指定", "Nao especificado"),
    "Dsm_TrendLowerLimit": ("下限", "Lower limit", "下限", "Limite inferior"),
    "Dsm_TrendUpperLimit": ("上限", "Upper limit", "上限", "Limite superior"),
    "Dsm_TrendYAxis": ("值", "Value", "値", "Valor"),
    "Dsm_DefaultValueName": ("值项{0}", "Value {0}", "値項目{0}", "Valor {0}"),
    "Dsm_DefaultEnumStateName": ("状态{0}", "State {0}", "状態{0}", "Estado {0}"),
    "Dsm_DataSourceSectionTitle": ("数据源", "Data sources", "データソース", "Fontes de dados"),
    "Dsm_DataSourceSectionHint": ("设备详情页展示已配置数据源的最新采样值", "Latest sampled values for configured sources", "設定済みデータソースの最新サンプル値", "Ultimos valores amostrados das fontes configuradas"),
    "Dsm_NoDevicesHint": ("请先添加设备", "Add a device first", "まず設備を追加してください", "Adicione um dispositivo primeiro"),
    "Dsm_UnitFormat": ("{0}", "{0}", "{0}", "{0}"),
    "Dsm_SourceFormat": ("{0}", "{0}", "{0}", "{0}"),
    "Dsm_TrendChart": ("趋势图", "Trend chart", "トレンド図", "Grafico de tendencia"),
    "Dsm_BackToLiveCard": ("返回实时卡片", "Back to live cards", "ライブカードに戻る", "Voltar aos cartoes ao vivo"),
    "Dsm_SourceSectionTitle": ("数据源列表", "Data sources", "データソース一覧", "Fontes de dados"),
    "Dsm_AddSource": ("添加数据源", "Add source", "データソース追加", "Adicionar fonte"),
    "Dsm_RemoveSource": ("删除数据源", "Remove source", "データソース削除", "Remover fonte"),
    "Dsm_Name": ("名称", "Name", "名前", "Nome"),
    "Dsm_Enabled": ("启用", "Enabled", "有効", "Ativado"),
    "Dsm_ValueSectionTitle": ("值项列表", "Values", "値項目一覧", "Valores"),
    "Dsm_AddValue": ("添加值项", "Add value", "値項目追加", "Adicionar valor"),
    "Dsm_RemoveValue": ("删除值项", "Remove value", "値項目削除", "Remover valor"),
    "Dsm_ExpectedValue": ("预期值", "Expected value", "期待値", "Valor esperado"),
    "Dsm_EnumTableTitle": ("枚举映射", "Enum mapping", "列挙マッピング", "Mapeamento de enum"),
    "Dsm_AddEnumValue": ("添加枚举", "Add enum", "列挙追加", "Adicionar enum"),
    "Dsm_RemoveEnumValue": ("删除枚举", "Remove enum", "列挙削除", "Remover enum"),
    "Dsm_EnumValue": ("枚举值", "Enum value", "列挙値", "Valor do enum"),
    "Dsm_DisplayName": ("显示名", "Display name", "表示名", "Nome de exibicao"),
    "Dsm_CollectionDescription": ("触发地址为空时按轮询周期定时采集；配置触发地址后仅在触发时采集。", "Empty trigger address uses periodic polling; configured trigger acquires on trigger only.", "トリガーアドレス未設定時は周期収集。設定時はトリガー時のみ収集。", "Sem endereco de gatilho usa coleta periodica; com gatilho coleta apenas no disparo."),
    "Validator_DeviceIdRequired": ("设备「{0}」未配置设备 ID", "Device '{0}' has no device ID", "設備「{0}」に設備 ID がありません", "Dispositivo '{0}' sem ID"),
    "Validator_DeviceIdDuplicate": ("设备 ID「{0}」重复", "Duplicate device ID '{0}'", "設備 ID「{0}」が重複しています", "ID de dispositivo duplicado '{0}'"),
    "Validator_SourceTriggerAddressInvalid": ("数据源「{0}」触发地址无效", "Invalid trigger address for source '{0}'", "データソース「{0}」のトリガーアドレスが無効です", "Endereco de gatilho invalido para fonte '{0}'"),
    "Validator_SourceNeedsValue": ("数据源「{0}」至少需要一个值项", "Source '{0}' needs at least one value", "データソース「{0}」には値項目が必要です", "Fonte '{0}' precisa de pelo menos um valor"),
    "Validator_SourceValueAddressMissing": ("数据源「{0}」值项「{1}」未配置采集地址", "Value '{1}' in source '{0}' has no PLC address", "データソース「{0}」値項目「{1}」に収集アドレスがありません", "Valor '{1}' na fonte '{0}' sem endereco PLC"),
    "Validator_SourceValueAddressInvalid": ("数据源「{0}」值项「{1}」采集地址无效", "Invalid PLC address for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」の収集アドレスが無効です", "Endereco PLC invalido para valor '{1}' na fonte '{0}'"),
    "Validator_SourceHysteresisNegative": ("数据源「{0}」值项「{1}」滞回不能为负数", "Hysteresis cannot be negative for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」のヒステリシスは負にできません", "Histerese nao pode ser negativa para valor '{1}' na fonte '{0}'"),
    "Validator_SourceConfirmSecondsNegative": ("数据源「{0}」值项「{1}」确认秒数不能为负数", "Confirm seconds cannot be negative for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」の確認秒数は負にできません", "Segundos de confirmacao nao podem ser negativos para valor '{1}' na fonte '{0}'"),
    "Validator_SourceValueAddressSameAsTrigger": ("数据源「{0}」值项「{1}」采集地址不能与触发地址相同", "Value '{1}' PLC address cannot match trigger address in source '{0}'", "データソース「{0}」値項目「{1}」の収集アドレスはトリガーアドレスと同じにできません", "Endereco PLC do valor '{1}' nao pode ser igual ao gatilho na fonte '{0}'"),
    "Validator_SourceDuplicateAddress": ("数据源「{0}」存在 {1} 个值项使用相同采集地址「{2}」", "Source '{0}' has {1} values sharing PLC address '{2}'", "データソース「{0}」に同一収集アドレス「{2}」を使う値項目が {1} 件あります", "Fonte '{0}' tem {1} valores com o mesmo endereco PLC '{2}'"),
    "Validator_AlarmKind": ("报警", "alarm", "アラーム", "alarme"),
    "Validator_DefectKind": ("缺陷", "defect", "不良", "defeito"),
    "Validator_CounterAlarmKind": ("计数报警", "counter alarm", "カウンタアラーム", "alarme de contador"),
    "Validator_EmptySourceName": ("设备「{0}」存在数据源名称为空", "Device '{0}' has a source with empty name", "設備「{0}」に名前の空のデータソースがあります", "Dispositivo '{0}' tem fonte com nome vazio"),
    "Validator_DuplicateSourceName": ("数据源名称「{0}」重复", "Duplicate source name '{0}'", "データソース名「{0}」が重複しています", "Nome de fonte duplicado '{0}'"),
    "Validator_SourceIdentityInvalid": ("数据源「{0}」标识无效", "Invalid identity for source '{0}'", "データソース「{0}」の識別子が無効です", "Identidade invalida para fonte '{0}'"),
    "Validator_EmptyValueName": ("数据源「{0}」存在值项名称为空", "Source '{0}' has a value with empty name", "データソース「{0}」に名前の空の値項目があります", "Fonte '{0}' tem valor com nome vazio"),
    "Validator_DuplicateValueName": ("数据源「{0}」值项名称「{1}」重复", "Duplicate value name '{1}' in source '{0}'", "データソース「{0}」の値項目名「{1}」が重複しています", "Nome de valor duplicado '{1}' na fonte '{0}'"),
    "Validator_EmptyValueId": ("数据源「{0}」存在值项 ID 为空", "Source '{0}' has a value with empty ID", "データソース「{0}」に ID の空の値項目があります", "Fonte '{0}' tem valor com ID vazio"),
    "Validator_StringLengthInvalid": ("数据源「{0}」值项「{1}」字符串长度无效（1-1024）", "Invalid string length (1-1024) for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」の文字列長が無効です（1-1024）", "Comprimento de string invalido (1-1024) para valor '{1}' na fonte '{0}'"),
    "Validator_FloatLimitsInvalid": ("数据源「{0}」值项「{1}」浮点上下限无效", "Invalid float limits for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」の浮動小数上下限が無効です", "Limites float invalidos para valor '{1}' na fonte '{0}'"),
    "Validator_LimitOrderInvalid": ("数据源「{0}」值项「{1}」上限必须大于下限", "Upper limit must be greater than lower limit for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」の上限は下限より大きくする必要があります", "Limite superior deve ser maior que o inferior para valor '{1}' na fonte '{0}'"),
    "Validator_LimitExpectedConflict": ("数据源「{0}」值项「{1}」不能同时配置上下限和预期值", "Cannot configure limits and expected value together for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」で上下限と期待値を同時に設定できません", "Nao e possivel configurar limites e valor esperado juntos para valor '{1}' na fonte '{0}'"),
    "Validator_AlarmParametersNegative": ("数据源「{0}」值项「{1}」滞回或确认秒数不能为负数", "Hysteresis or confirm seconds cannot be negative for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」のヒステリシスまたは確認秒数は負にできません", "Histerese ou segundos de confirmacao nao podem ser negativos para valor '{1}' na fonte '{0}'"),
    "Validator_EnumMappingInvalid": ("数据源「{0}」值项「{1}」枚举映射无效", "Invalid enum mapping for value '{1}' in source '{0}'", "データソース「{0}」値項目「{1}」の列挙マッピングが無効です", "Mapeamento de enum invalido para valor '{1}' na fonte '{0}'"),
    "Validator_TriggerAckConflict": ("数据源「{0}」触发值与确认值不能相同", "Trigger and ack values cannot match in source '{0}'", "データソース「{0}」のトリガー値と確認値は同じにできません", "Valores de gatilho e confirmacao nao podem ser iguais na fonte '{0}'"),
    "Validator_ChildIdentityInvalid": ("设备「{0}」{1}标识无效", "Invalid {1} identity for device '{0}'", "設備「{0}」の{1}識別子が無効です", "Identidade de {1} invalida para dispositivo '{0}'"),
    "Validator_EmptyChildName": ("设备「{0}」{1}名称为空", "Empty {1} name for device '{0}'", "設備「{0}」の{1}名が空です", "Nome de {1} vazio para dispositivo '{0}'"),
    "Validator_DuplicateChildId": ("设备「{0}」{1} ID 重复", "Duplicate {1} ID for device '{0}'", "設備「{0}」の{1} ID が重複しています", "ID de {1} duplicado para dispositivo '{0}'"),
    "F331": ("已导出 {0} 个数据源、{1} 个值项到 {2}", "Exported {0} sources and {1} values to {2}", "{0} 件のデータソースと {1} 件の値項目を {2} にエクスポートしました", "Exportadas {0} fontes e {1} valores para {2}"),
    "F333": ("CSV 文件为空或无数据行", "CSV file is empty or has no data rows", "CSV ファイルが空、またはデータ行がありません", "Arquivo CSV vazio ou sem linhas de dados"),
    "F334": ("第 {0} 行：数据源名称为空", "Row {0}: source name is empty", "{0} 行目: データソース名が空です", "Linha {0}: nome da fonte vazio"),
    "F335": ("第 {0} 行：值项名称为空", "Row {0}: value name is empty", "{0} 行目: 値項目名が空です", "Linha {0}: nome do valor vazio"),
    "F336": ("第 {0} 行：值项类型「{1}」无效", "Row {0}: invalid value type '{1}'", "{0} 行目: 値項目タイプ「{1}」が無効です", "Linha {0}: tipo de valor invalido '{1}'"),
    "F337": ("第 {0} 行：地址「{1}」无效（{2}）", "Row {0}: address '{1}' invalid ({2})", "{0} 行目: アドレス「{1}」が無効です（{2}）", "Linha {0}: endereco '{1}' invalido ({2})"),
    "F338": ("第 {0} 行：地址「{1}」类型不匹配，期望 {2}，实际 {3}", "Row {0}: address '{1}' type mismatch, expected {2}, actual {3}", "{0} 行目: アドレス「{1}」のタイプ不一致、期待 {2}、実際 {3}", "Linha {0}: tipo incompativel para endereco '{1}', esperado {2}, atual {3}"),
    "F339": ("第 {0} 行：字段「{1}」值「{2}」无效", "Row {0}: invalid value '{2}' for field '{1}'", "{0} 行目: フィールド「{1}」の値「{2}」が無効です", "Linha {0}: valor invalido '{2}' para campo '{1}'"),
    "F340": ("第 {0} 行：枚举 JSON 无效（{1}）", "Row {0}: invalid enum JSON ({1})", "{0} 行目: 列挙 JSON が無効です（{1}）", "Linha {0}: JSON de enum invalido ({1})"),
    "F341": ("第 {0} 行：枚举值重复", "Row {0}: duplicate enum values", "{0} 行目: 列挙値が重複しています", "Linha {0}: valores de enum duplicados"),
    "F342": ("第 {0} 行：同一数据源配置不一致", "Row {0}: inconsistent configuration for the same source", "{0} 行目: 同一データソースの設定が一致しません", "Linha {0}: configuracao inconsistente para a mesma fonte"),
    "F343": ("第 {0} 行：数据源「{1}」值项「{2}」重复", "Row {0}: duplicate value '{2}' in source '{1}'", "{0} 行目: データソース「{1}」の値項目「{2}」が重複しています", "Linha {0}: valor duplicado '{2}' na fonte '{1}'"),
    "F344": ("将导入 {0} 个数据源、{1} 个值项（当前设备有 {2} 个数据源）。", "Import {0} sources and {1} values (device currently has {2} sources).", "{0} 件のデータソースと {1} 件の値項目をインポートします（現在 {2} 件のデータソース）。", "Importar {0} fontes e {1} valores (dispositivo tem {2} fontes)."),
    "F345": ("已成功导入 {0} 个数据源、{1} 个值项。", "Imported {0} sources and {1} values.", "{0} 件のデータソースと {1} 件の値項目をインポートしました。", "Importadas {0} fontes e {1} valores."),
    "F346": ("缺少必需列：{0}", "Missing required columns: {0}", "必須列がありません: {0}", "Colunas obrigatorias ausentes: {0}"),
    "F347": ("已导入 {0} 个数据源、{1} 个值项，但有 {2} 个校验错误：\n{3}", "Imported {0} sources and {1} values with {2} validation errors:\n{3}", "{0} 件のデータソースと {1} 件の値項目をインポートしましたが、{2} 件の検証エラーがあります:\n{3}", "Importadas {0} fontes e {1} valores com {2} erros de validacao:\n{3}"),
    "F348": ("导入失败：{0}", "Import failed: {0}", "インポート失敗: {0}", "Falha na importacao: {0}"),
    "Ux_StatusUnsaved": ("有未保存的更改", "Unsaved changes", "未保存の変更があります", "Alteracoes nao salvas"),
    "Ux_StatusSaved": ("已保存", "Saved", "保存済み", "Salvo"),
    "Ux_StatusSaving": ("正在保存…", "Saving…", "保存中…", "Salvando…"),
    "Ux_DeviceWizardCreated": ("已创建设备「{0}」", "Created device '{0}'", "設備「{0}」を作成しました", "Dispositivo '{0}' criado"),
    "Ux_DeviceWizardTitle": ("新建设备向导", "New Device Wizard", "新規設備ウィザード", "Assistente de novo dispositivo"),
    "Ux_DeviceWizardStepBasic": ("基本信息", "Basic info", "基本情報", "Informacoes basicas"),
    "Ux_DeviceWizardStepAddresses": ("PLC 地址", "PLC addresses", "PLC アドレス", "Enderecos PLC"),
    "Ux_DeviceWizardStepReview": ("确认", "Review", "確認", "Revisao"),
    "Ux_DeviceWizardIntro": ("填写设备基本信息。完成后可继续配置 PLC 地址。", "Enter basic device information. You can configure PLC addresses next.", "設備の基本情報を入力します。次に PLC アドレスを設定できます。", "Informe os dados basicos do dispositivo. Em seguida configure os enderecos PLC."),
    "Ux_DeviceWizardMachineType": ("机型", "Machine type", "機種", "Tipo de maquina"),
    "Ux_DeviceWizardTargetCycleHint": ("目标周期用于 OEE 性能率计算，单位毫秒。", "Target cycle is used for OEE performance rate (milliseconds).", "目標サイクルは OEE 性能率計算に使用します（ミリ秒）。", "Ciclo alvo usado na taxa de performance OEE (milissegundos)."),
    "Ux_DeviceWizardAddressHint": ("配置产量、状态等 PLC 地址。可跳过，稍后在设备管理中补充。", "Configure production and status PLC addresses. You can skip and add them later.", "生産・状態などの PLC アドレスを設定します。後から追加も可能です。", "Configure enderecos PLC de producao e status. Pode pular e adicionar depois."),
    "Ux_DeviceWizardAddressRequired": ("请至少配置一个 PLC 地址，或返回上一步。", "Configure at least one PLC address or go back.", "少なくとも 1 つの PLC アドレスを設定するか、戻ってください。", "Configure pelo menos um endereco PLC ou volte."),
    "Ux_DeviceWizardReviewHint": ("确认信息无误后点击完成创建设备。", "Review the information, then click Finish to create the device.", "内容を確認し、完了をクリックして設備を作成します。", "Revise as informacoes e clique em Concluir para criar o dispositivo."),
    "Ux_DeviceWizardBack": ("上一步", "Back", "戻る", "Voltar"),
    "Ux_DeviceWizardNext": ("下一步", "Next", "次へ", "Avancar"),
    "Ux_DeviceWizardFinish": ("完成", "Finish", "完了", "Concluir"),
    "Ux_DeviceWizardNameRequired": ("设备名称不能为空", "Device name is required", "設備名は必須です", "Nome do dispositivo obrigatorio"),
    "Ux_DeviceWizardNameDuplicate": ("设备名称已存在", "Device name already exists", "設備名は既に存在します", "Nome do dispositivo ja existe"),
    "Ux_DeviceWizardTargetCycleInvalid": ("目标周期必须大于 0", "Target cycle must be greater than 0", "目標サイクルは 0 より大きくする必要があります", "Ciclo alvo deve ser maior que 0"),
    "Ux_ResetQuery": ("已重置查询条件", "Query filters reset", "検索条件をリセットしました", "Filtros de consulta redefinidos"),
    "Ux_Querying": ("正在查询…", "Querying…", "照会中…", "Consultando…"),
    "Ux_QueryFinished": ("查询完成，共 {0} 条记录", "Query finished, {0} records", "照会完了、{0} 件", "Consulta concluida, {0} registros"),
    "Ux_ConfirmPassword": ("确认密码", "Confirm password", "パスワード確認", "Confirmar senha"),
    "Settings_CollectorLocalMode": ("本地模式", "Local mode", "ローカルモード", "Modo local"),
    "Settings_CollectorConfirmed": ("已同步到 Collector", "Synced to Collector", "Collector に同期済み", "Sincronizado com o Collector"),
    "Settings_CollectorPending": ("待同步到 Collector", "Pending sync to Collector", "Collector 同期待ち", "Sincronizacao com Collector pendente"),
    "Settings_CollectorNotConnectedPending": ("Collector 未连接，配置已保存但未同步", "Collector not connected; settings saved but not synced", "Collector 未接続。設定は保存済みですが未同期です", "Collector desconectado; configuracoes salvas mas nao sincronizadas"),
    "Settings_CollectorNotConnected": ("Collector 未连接", "Collector not connected", "Collector 未接続", "Collector desconectado"),
    "Rtmon_HistoryQueueSummary": ("生产队列峰值 {0}、溢出 {1}；数据源队列峰值 {2}、溢出 {3}", "Production queue peak {0}, overflow {1}; data source queue peak {2}, overflow {3}", "生産キュー峰值 {0}、オーバーフロー {1}；データソースキュー峰值 {2}、オーバーフロー {3}", "Pico fila producao {0}, overflow {1}; pico fila fontes {2}, overflow {3}"),
    "Rtmon_HistoryFlushSummary": ("生产刷盘 P95 {0}ms P99 {1}ms 失败 {2}；数据源 P95 {3}ms P99 {4}ms 失败 {5}", "Production flush P95 {0}ms P99 {1}ms failures {2}; data source P95 {3}ms P99 {4}ms failures {5}", "生産フラッシュ P95 {0}ms P99 {1}ms 失敗 {2}；データソース P95 {3}ms P99 {4}ms 失敗 {5}", "Flush producao P95 {0}ms P99 {1}ms falhas {2}; fontes P95 {3}ms P99 {4}ms falhas {5}"),
    "Rtmon_P95Cycle": ("P95 周期", "P95 cycle", "P95 サイクル", "Ciclo P95"),
    "Rtmon_P99Cycle": ("P99 周期", "P99 cycle", "P99 サイクル", "Ciclo P99"),
    "Rtmon_DataSourcePending": ("数据源待写入", "Data source pending writes", "データソース書込待ち", "Escritas pendentes de fontes"),
    "Rtmon_DataSourceRecovery": ("数据源恢复文件", "Data source recovery file", "データソース復旧ファイル", "Arquivo de recuperacao de fontes"),
    "Rtmon_QueuePeakOverflow": ("队列峰值/溢出", "Queue peak/overflow", "キュー峰值/オーバーフロー", "Pico/overflow da fila"),
    "Rtmon_FlushLatency": ("刷盘延迟", "Flush latency", "フラッシュ遅延", "Latencia de flush"),
    "Rtmon_DataSourceDatabase": ("数据源数据库", "Data source database", "データソース DB", "Banco de fontes de dados"),
    "Rtmon_TotalDatabase": ("总数据库", "Total database", "合計 DB", "Banco total"),
}

KEY_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
KEY_PREFIXES = (
    "Alarm_",
    "Csv_",
    "Dsm_",
    "F",
    "K",
    "M",
    "Nav_",
    "Rtmon_",
    "Settings_",
    "Ux_",
    "Validator_",
    "Web_",
    "Badge_",
    "Card_",
    "Conn_",
    "Fresh_",
    "Lbl_",
    "Meta_",
    "Msg_",
    "Status_",
    "Val_",
    "Wo_",
    "Common_",
    "Language_",
)
SCAN_PATTERNS = (
    re.compile(r"Strings\.([A-Za-z_][A-Za-z0-9_]*)"),
    re.compile(r"ConverterParameter=([A-Za-z_][A-Za-z0-9_]*)"),
    re.compile(r'HeaderAliases\("([A-Za-z_][A-Za-z0-9_]*)"'),
    re.compile(r'"([A-Za-z_][A-Za-z0-9_]*)"\s*:\s*"Csv_'),
)


def is_localization_key(key: str) -> bool:
    if not KEY_PATTERN.match(key):
        return False
    if key in TRANSLATIONS:
        return True
    return any(key.startswith(prefix) for prefix in KEY_PREFIXES)


def discover_keys() -> set[str]:
    keys: set[str] = set()
    for folder in (ROOT / "MainAPP", ROOT / "MainAPP.Tests"):
        if not folder.exists():
            continue
        for path in folder.rglob("*"):
            if path.suffix not in {".cs", ".xaml"}:
                continue
            text = path.read_text(encoding="utf-8")
            for pattern in SCAN_PATTERNS:
                keys.update(pattern.findall(text))
    return keys


def load_existing_keys() -> set[str]:
    with CSV_PATH.open(encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        return {row["Key"] for row in reader if row.get("Resource") == "Wpf"}


def humanize(key: str) -> str:
    return key.replace("_", " ")


def append_rows(missing: list[str]) -> int:
    with CSV_PATH.open("a", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle)
        for key in missing:
            if key in TRANSLATIONS:
                zh, en, ja, pt = TRANSLATIONS[key]
            else:
                label = humanize(key)
                zh = en = ja = pt = label
                print(f"warning: no translation for {key}, using placeholder", file=sys.stderr)
            writer.writerow(["Wpf", key, zh, en, ja, pt])
    return len(missing)


def main() -> int:
    referenced = discover_keys()
    existing = load_existing_keys()
    missing = sorted(key for key in referenced if key not in existing and is_localization_key(key))
    if not missing:
        print("No missing Wpf localization keys.")
        return 0
    added = append_rows(missing)
    print(f"Added {added} missing localization rows to {CSV_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
