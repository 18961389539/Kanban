#!/usr/bin/env python3
"""Fill NameEn/NameJa/NamePt (and DisplayName*) in devices.json from Chinese Name fields."""

from __future__ import annotations

import json
import os
import re
import shutil
import sys
from pathlib import Path

# Base translations: Chinese -> (English, Japanese, Portuguese)
BASE: dict[str, tuple[str, str, str]] = {
    "NG偏差计数": ("NG Deviation Count", "NG偏差カウント", "Contagem desvio NG"),
    "NG率超限计数": ("NG Rate Over-limit Count", "NG率上限超過カウント", "Contagem taxa NG acima do limite"),
    "OEE偏低计数": ("Low OEE Count", "OEE偏低カウント", "Contagem OEE baixo"),
    "OK偏差计数": ("OK Deviation Count", "OK偏差カウント", "Contagem desvio OK"),
    "产量偏差计数": ("Output Deviation Count", "生産量偏差カウント", "Contagem desvio producao"),
    "人工停机次数": ("Manual Stop Count", "手動停止回数", "Contagem parada manual"),
    "传感器故障停机": ("Sensor Fault Shutdown", "センサ故障停止", "Parada falha sensor"),
    "传感器漂移": ("Sensor Drift", "センサドリフト", "Deriva do sensor"),
    "伺服报警": ("Servo Alarm", "サーボアラーム", "Alarme servo"),
    "位置度超差": ("Position Out of Tolerance", "位置度公差外れ", "Posicao fora de tolerancia"),
    "保养停机次数": ("Maintenance Stop Count", "保全停止回数", "Contagem parada manutencao"),
    "保养周期计数": ("Maintenance Cycle Count", "保全周期カウント", "Contagem ciclo manutencao"),
    "保养异常计数": ("Maintenance Anomaly Count", "保全異常カウント", "Contagem anomalia manutencao"),
    "保压不足": ("Insufficient Packing Pressure", "保圧不足", "Pressao de recalque insuficiente"),
    "值1": ("Value 1", "値1", "Valor 1"),
    "停机率超限计数": ("Downtime Rate Over-limit Count", "停止率上限超過カウント", "Contagem taxa parada acima do limite"),
    "全检不合格计数": ("Full Inspection Fail Count", "全数検査不合格カウント", "Contagem reprovacao inspecao total"),
    "冷却水流量低": ("Low Cooling Water Flow", "冷却水流量低下", "Fluxo baixo agua resfriamento"),
    "刀具磨损计数": ("Tool Wear Count", "刃具摩耗カウント", "Contagem desgaste ferramenta"),
    "划痕": ("Scratch", "キズ", "Arranhao"),
    "功能失效": ("Function Failure", "機能失效", "Falha funcional"),
    "加热圈断路": ("Heater Circuit Open", "ヒータ断線", "Circuito aquecedor aberto"),
    "包装破损": ("Packaging Damage", "包装破損", "Embalagem danificada"),
    "卡滞": ("Sticking/Jam", "固着", "Travamento"),
    "压力偏低": ("Low Pressure", "圧力偏低", "Pressao baixa"),
    "压力超限": ("Pressure Over Limit", "圧力上限超過", "Pressao acima do limite"),
    "压力超限计数": ("Pressure Over-limit Count", "圧力上限超過カウント", "Contagem pressao acima limite"),
    "压力过高": ("Pressure Too High", "圧力過高", "Pressao alta"),
    "厚度超差": ("Thickness Out of Tolerance", "厚さ公差外れ", "Espessura fora tolerancia"),
    "变形": ("Deformation", "変形", "Deformacao"),
    "变频器故障": ("Inverter Fault", "インバータ故障", "Falha inversor"),
    "同心度超差": ("Concentricity Out of Tolerance", "同心度公差外れ", "Concentricidade fora tolerancia"),
    "周产量计数": ("Weekly Output Count", "週産量カウント", "Contagem producao semanal"),
    "噪音异常": ("Abnormal Noise", "騒音異常", "Ruido anormal"),
    "噪音超限计数": ("Noise Over-limit Count", "騒音上限超過カウント", "Contagem ruido acima limite"),
    "圆度超差": ("Roundness Out of Tolerance", "真円度公差外れ", "Circularidade fora tolerancia"),
    "复检次数计数": ("Re-inspection Count", "再検査回数カウント", "Contagem reinspecao"),
    "复测次数计数": ("Retest Count", "再測定回数カウント", "Contagem reteste"),
    "安全门未关": ("Safety Door Open", "安全扉未閉", "Porta seguranca aberta"),
    "定位偏差计数": ("Position Deviation Count", "位置偏差カウント", "Contagem desvio posicao"),
    "密封不良": ("Poor Sealing", "密封不良", "Vedacao deficiente"),
    "密封件寿命计数": ("Seal Life Count", "シール寿命カウント", "Contagem vida vedacao"),
    "对中偏差计数": ("Alignment Deviation Count", "芯出し偏差カウント", "Contagem desvio alinhamento"),
    "封口不良": ("Poor Sealing (Pack)", "封口不良", "Selagem deficiente"),
    "射出超时": ("Injection Timeout", "射出タイムアウト", "Timeout injecao"),
    "尺寸偏大": ("Oversize", "寸法過大", "Dimensao grande"),
    "尺寸偏小": ("Undersize", "寸法過小", "Dimensao pequena"),
    "巡检异常计数": ("Patrol Inspection Anomaly Count", "巡回点検異常カウント", "Contagem anomalia inspecao"),
    "平面度超差": ("Flatness Out of Tolerance", "平面度公差外れ", "Planicidade fora tolerancia"),
    "异常停机次数": ("Abnormal Stop Count", "異常停止回数", "Contagem parada anormal"),
    "待机时间累计": ("Standby Time Accumulated", "待機時間累計", "Tempo espera acumulado"),
    "急停按下": ("Emergency Stop Pressed", "非常停止押下", "Parada emergencia acionada"),
    "扭矩不足": ("Insufficient Torque", "トルク不足", "Torque insuficiente"),
    "报废次数计数": ("Scrap Count", "廃棄回数カウント", "Contagem sucata"),
    "报警停机次数": ("Alarm Stop Count", "アラーム停止回数", "Contagem parada alarme"),
    "报警次数累计": ("Alarm Count Accumulated", "アラーム回数累計", "Contagem alarmes acumulada"),
    "抽检不合格计数": ("Sampling Fail Count", "抜取検査不合格カウント", "Contagem reprovacao amostragem"),
    "振动异常": ("Abnormal Vibration", "振動異常", "Vibracao anormal"),
    "振动超限计数": ("Vibration Over-limit Count", "振動上限超過カウント", "Contagem vibracao acima limite"),
    "换模停机次数": ("Mold Change Stop Count", "金型交換停止回数", "Contagem parada troca molde"),
    "换模次数计数": ("Mold Change Count", "金型交換回数カウント", "Contagem troca molde"),
    "接触不良": ("Poor Contact", "接触不良", "Contato deficiente"),
    "故障停机次数": ("Fault Stop Count", "故障停止回数", "Contagem parada falha"),
    "故障次数累计": ("Fault Count Accumulated", "故障回数累計", "Contagem falhas acumulada"),
    "斑点": ("Spot/Stain", "斑点", "Mancha"),
    "料斗空": ("Hopper Empty", "ホッパ空", "Funil vazio"),
    "料筒磨损": ("Barrel Wear", "シリンダ摩耗", "Desgaste cilindro"),
    "断气停机次数": ("Air Loss Stop Count", "エア断停止回数", "Contagem parada perda ar"),
    "断水停机次数": ("Water Loss Stop Count", "断水停止回数", "Contagem parada perda agua"),
    "断电停机次数": ("Power Loss Stop Count", "停電停止回数", "Contagem parada falta energia"),
    "断路": ("Circuit Open", "断線", "Circuito aberto"),
    "日产量计数": ("Daily Output Count", "日産量カウント", "Contagem producao diaria"),
    "时产量计数": ("Hourly Output Count", "時間産量カウント", "Contagem producao horaria"),
    "时间偏差": ("Time Deviation", "時間偏差", "Desvio tempo"),
    "月产量计数": ("Monthly Output Count", "月産量カウント", "Contagem producao mensal"),
    "杂质": ("Impurity", "異物", "Impureza"),
    "标定偏差计数": ("Calibration Deviation Count", "校正偏差カウント", "Contagem desvio calibracao"),
    "标签偏移": ("Label Offset", "ラベルずれ", "Deslocamento rotulo"),
    "校准偏差计数": ("Calibration Deviation Count", "キャリブレーション偏差カウント", "Contagem desvio calibracao"),
    "模具保养计数": ("Mold Maintenance Count", "金型保全カウント", "Contagem manutencao molde"),
    "模具寿命计数": ("Mold Life Count", "金型寿命カウント", "Contagem vida util molde"),
    "模具磨损": ("Mold Wear", "金型摩耗", "Desgaste molde"),
    "毛刺": ("Burr", "バリ", "Rebarba"),
    "气压低": ("Low Air Pressure", "空気圧低下", "Pressao ar baixa"),
    "气泡": ("Bubble/Void", "気泡", "Bolha"),
    "氧化": ("Oxidation", "酸化", "Oxidacao"),
    "水印": ("Watermark", "ウォーターマーク", "Marca d'agua"),
    "油位低": ("Low Oil Level", "油面低下", "Nivel oleo baixo"),
    "油温偏高": ("High Oil Temperature", "油温偏高", "Temperatura oleo alta"),
    "注塑机1": ("Injection Molder 1", "射出成形機1", "Injetora 1"),
    "注塑机2": ("Injection Molder 2", "射出成形機2", "Injetora 2"),
    "流痕": ("Flow Mark", "流れ跡", "Marca de fluxo"),
    "润滑不足": ("Insufficient Lubrication", "潤滑不足", "Lubricacao insuficiente"),
    "润滑低": ("Low Lubrication", "潤滑低下", "Lubricacao baixa"),
    "液位低": ("Low Liquid Level", "液面低下", "Nivel liquido baixo"),
    "液压故障停机": ("Hydraulic Fault Shutdown", "油圧故障停止", "Parada falha hidraulica"),
    "液压泵过载": ("Hydraulic Pump Overload", "油圧ポンプ過負荷", "Sobrecarga bomba hidraulica"),
    "温度偏低": ("Low Temperature", "温度偏低", "Temperatura baixa"),
    "温度异常": ("Temperature Abnormal", "温度異常", "Temperatura anormal"),
    "温度触发采集": ("Temperature-triggered Acquisition", "温度トリガー収集", "Aquisicao por temperatura"),
    "温度超限计数": ("Temperature Over-limit Count", "温度上限超過カウント", "Contagem temperatura acima limite"),
    "温度过高": ("Over Temperature", "温度過高", "Temperatura alta"),
    "温控偏差": ("Temperature Control Deviation", "温度制御偏差", "Desvio controle temperatura"),
    "滤网堵塞": ("Filter Clogged", "フィルター詰まり", "Filtro entupido"),
    "点检异常计数": ("Inspection Anomaly Count", "点検異常カウント", "Contagem anomalia inspecao"),
    "烧焦": ("Burn Mark", "焼け", "Queimadura"),
    "照明故障": ("Lighting Fault", "照明故障", "Falha iluminacao"),
    "熔接痕": ("Weld Line", "溶接線", "Linha de solda"),
    "物料不良计数": ("Material Defect Count", "物料不良カウント", "Contagem defeito material"),
    "物料混料计数": ("Material Mix Count", "混料カウント", "Contagem mistura material"),
    "物料短缺计数": ("Material Shortage Count", "物料不足カウント", "Contagem falta material"),
    "物料超期计数": ("Material Expired Count", "物料期限切れカウント", "Contagem material vencido"),
    "物料错料计数": ("Wrong Material Count", "誤物料カウント", "Contagem material errado"),
    "班产量计数": ("Shift Output Count", "班産量カウント", "Contagem producao turno"),
    "班次NG计数": ("Shift NG Count", "班次NGカウント", "Contagem NG turno"),
    "班次不合格计数": ("Shift Fail Count", "班次不合格カウント", "Contagem reprovacao turno"),
    "班次不良计数": ("Shift Defect Count", "班次不良カウント", "Contagem defeito turno"),
    "班次缺陷计数": ("Shift Flaw Count", "班次欠陥カウント", "Contagem falha turno"),
    "班次超差计数": ("Shift Out-of-tolerance Count", "班次公差外れカウント", "Contagem fora tolerancia turno"),
    "电压波动": ("Voltage Fluctuation", "電圧変動", "Flutuacao tensao"),
    "电机故障停机": ("Motor Fault Shutdown", "モータ故障停止", "Parada falha motor"),
    "电气故障停机": ("Electrical Fault Shutdown", "電気故障停止", "Parada falha eletrica"),
    "电气短路": ("Electrical Short Circuit", "電気短絡", "Curto-circuito"),
    "电流超限计数": ("Current Over-limit Count", "電流上限超過カウント", "Contagem corrente acima limite"),
    "电流过载": ("Current Overload", "電流过負荷", "Sobrecarga corrente"),
    "积碳": ("Carbon Deposit", "カーボン付着", "Deposito carbono"),
    "粗糙度超差": ("Roughness Out of Tolerance", "粗さ公差外れ", "Rugosidade fora tolerancia"),
    "紧急停机次数": ("Emergency Stop Count", "非常停止回数", "Contagem parada emergencia"),
    "累计冷却水": ("Cooling Water Accumulated", "冷却水累計", "Agua resfriamento acumulada"),
    "累计加工件数": ("Processed Parts Accumulated", "加工件数累計", "Pecas processadas acumuladas"),
    "累计压缩空气": ("Compressed Air Accumulated", "圧縮空気累計", "Ar comprimido acumulado"),
    "累计能耗值": ("Energy Consumption Accumulated", "累計エネルギー", "Energia acumulada"),
    "累计运行模次": ("Total Mold Cycles", "累計成形回数", "Ciclos acumulados"),
    "组装机1": ("Assembly Machine 1", "組立機1", "Maquina montagem 1"),
    "缩水": ("Shrinkage", "収縮", "Retracao"),
    "缺件": ("Missing Part", "欠品", "Peca faltando"),
    "缺料停机次数": ("Material Shortage Stop Count", "欠材停止回数", "Contagem parada falta material"),
    "缺附件": ("Missing Accessory", "付属品不足", "Acessorio faltando"),
    "联锁停机次数": ("Interlock Stop Count", "インターロック停止回数", "Contagem parada intertravamento"),
    "背压异常": ("Back Pressure Abnormal", "背圧異常", "Contra-pressao anormal"),
    "能耗偏高": ("High Energy Consumption", "エネルギー消費偏高", "Consumo energia alto"),
    "能耗累计": ("Energy Consumption Accumulated", "エネルギー累計", "Energia acumulada"),
    "良率偏低计数": ("Low Yield Count", "良品率偏低カウント", "Contagem rendimento baixo"),
    "良率偏差计数": ("Yield Deviation Count", "良品率偏差カウント", "Contagem desvio rendimento"),
    "色差": ("Color Difference", "色差", "Diferenca de cor"),
    "节拍偏低计数": ("Low Cycle Time Count", "タクト偏低カウント", "Contagem ciclo baixo"),
    "节拍偏差计数": ("Cycle Time Deviation Count", "タクト偏差カウント", "Contagem desvio ciclo"),
    "螺杆卡死": ("Screw Jammed", "スクリュー固着", "Parafuso travado"),
    "螺杆异常": ("Screw Abnormal", "スクリュー異常", "Parafuso anormal"),
    "螺纹超差": ("Thread Out of Tolerance", "ねじ公差外れ", "Rosca fora tolerancia"),
    "装配松动": ("Loose Assembly", "組立緩み", "Montagem frouxa"),
    "褪色": ("Fading/Discoloration", "褪色", "Desbotamento"),
    "角度偏差计数": ("Angle Deviation Count", "角度偏差カウント", "Contagem desvio angulo"),
    "角度超差": ("Angle Out of Tolerance", "角度公差外れ", "Angulo fora tolerancia"),
    "计数器偏差": ("Counter Deviation", "カウンタ偏差", "Desvio contador"),
    "设备点检计数": ("Equipment Inspection Count", "設備点検カウント", "Contagem inspecao equipamento"),
    "贴标歪斜": ("Label Skew", "ラベル歪み", "Rotulo torto"),
    "起皱": ("Wrinkle", "しわ", "Enrugamento"),
    "超压停机次数": ("Over-pressure Stop Count", "過圧停止回数", "Contagem parada sobrepresao"),
    "超温停机次数": ("Over-temperature Stop Count", "過温停止回数", "Contagem parada sobretemperatura"),
    "车间温度": ("Shop Floor Temperature", "工場温度", "Temperatura chao fabrica"),
    "车间湿度": ("Shop Floor Humidity", "工場湿度", "Umidade chao fabrica"),
    "轴承寿命计数": ("Bearing Life Count", "軸受寿命カウント", "Contagem vida rolamento"),
    "过载停机次数": ("Overload Stop Count", "過負荷停止回数", "Contagem parada sobrecarga"),
    "运行时间累计": ("Runtime Accumulated", "稼働時間累計", "Tempo operacao acumulado"),
    "返工次数计数": ("Rework Count", "再加工回数カウント", "Contagem retrabalho"),
    "连续NG次数": ("Consecutive NG Count", "連続NG回数", "Contagem NG consecutivos"),
    "连续不合格数": ("Consecutive Fail Count", "連続不合格数", "Reprovacoes consecutivas"),
    "连续不良数": ("Consecutive Defect Count", "連続不良数", "Defeitos consecutivos"),
    "连续缺陷数": ("Consecutive Flaw Count", "連続欠陥数", "Falhas consecutivas"),
    "连续超差数": ("Consecutive Out-of-tolerance Count", "連続公差外れ数", "Fora tolerancia consecutivo"),
    "通讯中断停机": ("Communication Loss Shutdown", "通信中断停止", "Parada perda comunicacao"),
    "通讯抖动": ("Communication Jitter", "通信ジッター", "Jitter comunicacao"),
    "银纹": ("Silver Streak", "シルバーストリーク", "Estria prateada"),
    "锁模力不足": ("Insufficient Clamping Force", "型締力不足", "Forca fechamento insuficiente"),
    "错件": ("Wrong Part", "誤組み", "Peca errada"),
    "雾化": ("Fogging", "曇り", "Embacamento"),
    "预测性保养计数": ("Predictive Maintenance Count", "予知保全カウント", "Contagem manutencao preditiva"),
    "预防性保养计数": ("Preventive Maintenance Count", "予防保全カウント", "Contagem manutencao preventiva"),
    "频率偏差": ("Frequency Deviation", "周波数偏差", "Desvio frequencia"),
    "风扇异常": ("Fan Abnormal", "ファン異常", "Ventilador anormal"),
    "飞边": ("Flash/Burr Flash", "バリ", "Rebarba flash"),
    "首件检验计数": ("First Article Inspection Count", "初物検査カウント", "Contagem inspecao primeira peca"),
    "黑点": ("Black Spot", "黒点", "Mancha preta"),
}

# Pattern-based suffix translations
_SUFFIX_RE = re.compile(r"^(?P<base>.+)-(?P<n>\d+)$")
_NUM_SUFFIX_RE = re.compile(r"^(?P<base>.+?)(?P<n>\d+)$")

_PATTERN_BASE: dict[str, tuple[str, str, str]] = {
    "其他异常": ("Other Anomaly", "その他異常", "Outra anomalia"),
    "班次产量": ("Shift Output", "班次生産量", "Producao turno"),
    "模具次数": ("Mold Count", "金型回数", "Contagem molde"),
}


def translate(name: str) -> tuple[str, str, str] | None:
    if not name or not name.strip():
        return None
    name = name.strip()
    if name in BASE:
        return BASE[name]

    m = _SUFFIX_RE.match(name)
    if m:
        base = m.group("base")
        n = m.group("n")
        base_tr = translate(base)
        if base_tr:
            en, ja, pt = base_tr
            return (f"{en}-{n}", f"{ja}-{n}", f"{pt}-{n}")

    for prefix, tr in _PATTERN_BASE.items():
        if name.startswith(prefix):
            rest = name[len(prefix) :]
            if rest.isdigit():
                en, ja, pt = tr
                return (f"{en} {rest}", f"{ja}{rest}", f"{pt} {rest}")

    return None


I18N_KEYS = (
    ("NameEn", "NameJa", "NamePt"),
    ("DisplayNameEn", "DisplayNameJa", "DisplayNamePt"),
)


def apply_i18n(obj: dict) -> int:
    updated = 0
    name = obj.get("Name") or obj.get("DisplayName")
    if not name:
        return 0
    tr = translate(str(name))
    if not tr:
        return 0
    en, ja, pt = tr
    if "Name" in obj or "NameEn" in obj:
        if not obj.get("NameEn"):
            obj["NameEn"] = en
            updated += 1
        if not obj.get("NameJa"):
            obj["NameJa"] = ja
            updated += 1
        if not obj.get("NamePt"):
            obj["NamePt"] = pt
            updated += 1
    if "DisplayName" in obj or "DisplayNameEn" in obj:
        if not obj.get("DisplayNameEn"):
            obj["DisplayNameEn"] = en
            updated += 1
        if not obj.get("DisplayNameJa"):
            obj["DisplayNameJa"] = ja
            updated += 1
        if not obj.get("DisplayNamePt"):
            obj["DisplayNamePt"] = pt
            updated += 1
    return updated


def walk(node, missing: list[str]) -> int:
    total = 0
    if isinstance(node, dict):
        name = node.get("Name") or node.get("DisplayName")
        if name and (node.get("NameEn") is None or node.get("NameEn") == "" or node.get("DisplayNameEn") is None):
            before = apply_i18n(node)
            if before == 0 and name:
                missing.append(str(name))
            total += before
        for v in node.values():
            total += walk(v, missing)
    elif isinstance(node, list):
        for item in node:
            total += walk(item, missing)
    return total


def main() -> int:
    data_root = os.environ.get("KANBAN_DATA_DIR") or os.path.join(os.environ["APPDATA"], "Kanban")
    path = Path(data_root) / "Config" / "devices.json"
    if len(sys.argv) > 1:
        path = Path(sys.argv[1])
    if not path.exists():
        print(f"Not found: {path}", file=sys.stderr)
        return 1

    with path.open(encoding="utf-8") as f:
        data = json.load(f)

    missing: list[str] = []
    updated = walk(data, missing)

    backup = path.with_suffix(path.suffix + ".bak")
    shutil.copy2(path, backup)
    with path.open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print(f"Updated fields: {updated}")
    print(f"Backup: {backup}")
    if missing:
        uniq = sorted(set(missing))
        print(f"Untranslated names ({len(uniq)}):")
        for n in uniq[:20]:
            print(f"  - {n}")
        if len(uniq) > 20:
            print(f"  ... and {len(uniq) - 20} more")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
