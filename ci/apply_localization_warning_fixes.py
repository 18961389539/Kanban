#!/usr/bin/env python3
"""Apply Visual Studio resx punctuation/whitespace warning fixes to Localization.csv."""

from __future__ import annotations

import csv
from pathlib import Path

CSV_PATH = Path("MainAPP/Resources/Localization.csv")
FIELDNAMES = ["Resource", "Key", "zh-CN", "en-US", "ja-JP", "pt-BR"]

# key -> {language: value}
FIXES: dict[str, dict[str, str]] = {
    "K008": {"en-US": "Work Order No", "pt-BR": "Nº do pedido"},
    "Web_Wo_OrderNo": {"en-US": "Order No", "pt-BR": "Nº do pedido"},
    "K183": {"en-US": "Current value:", "pt-BR": "Valor atual:"},
    "K415": {"en-US": "Current Shift:", "pt-BR": "Turno atual:"},
    "K474": {"en-US": "Machine Code:", "pt-BR": "Código da máquina:"},
    "K475": {"en-US": "License Type:", "pt-BR": "Tipo de licença:"},
    "K476": {"en-US": "Activation Time:", "pt-BR": "Hora de ativação:"},
    "K477": {"en-US": "Expiry Time:", "pt-BR": "Hora de expiração:"},
    "K478": {"en-US": "License Key:", "pt-BR": "Chave de licença:"},
    "M166": {"en-US": "Address conflict「", "pt-BR": "Conflito de endereço「"},
    "K692": {"en-US": "(Copy)", "pt-BR": "(Cópia)"},
    "F042": {
        "en-US": "· Expires {0:yyyy-MM-dd}",
        "ja-JP": "· 有効期限 {0:yyyy-MM-dd}",
        "pt-BR": "· Expira em {0:yyyy-MM-dd}",
    },
    "K686": {"en-US": "Applying recipe…", "pt-BR": "Aplicando receita…"},
    "F059": {
        "en-US": "Main defect is {0} ({1}), cumulative {2:N0} pcs, {3:F1}%",
        "pt-BR": "Defeito principal: {0} ({1}), acumulado {2:N0} pcs, {3:F1}%",
    },
    "Web_Rv_TopDefectConclusion": {
        "en-US": "Top defect: {0} ({1}), {2:N0} pcs, {3:F1}%",
        "pt-BR": "Defeito principal: {0} ({1}), {2:N0} pcs, {3:F1}%",
    },
    "F195": {
        "en-US": "Takt anomaly: actual {0:F1} pcs/hour, target {1:F0} pcs/hour, below 80%",
        "ja-JP": "タクト異常: 実績 {0:F1} 個/時、目標 {1:F0} 個/時の 80%",
        "pt-BR": "Anomalia de takt: real {0:F1} pcs/h, meta {1:F0} pcs/h, abaixo de 80%",
    },
    "Web_Rv_HealthLowOutput": {
        "en-US": "Cycle anomaly: actual {0:F1} pcs/h, target {1:F0} pcs/h, below 80%",
        "ja-JP": "タクト異常：実績 {0:F1} 個/時、目標 {1:F0} 個/時の 80%",
        "pt-BR": "Anomalia de ciclo: real {0:F1} pcs/h, meta {1:F0} pcs/h, abaixo de 80%",
    },
    "K142": {"ja-JP": "エラー項目をダブルクリックして対応する設備とタブに移動。"},
    "K210": {
        "ja-JP": "シフト切替または手動クリア時にこのアドレスへ1を書込み、PLCプログラムがOK/NGカウンタをクリアします。",
    },
    "K315": {"ja-JP": "設備と時間範囲を選択して「照会」をクリックすると、その期間のOEE指標を計算します。"},
    "K433": {
        "ja-JP": "実周期はPLC読取りとポーリング待ちを含みます。最大周期が設定間隔を超え続ける場合はPLC応答・ネットワーク・アドレス数を確認してください。",
    },
    "K439": {
        "ja-JP": "直近成功読取りは前ラウンドで少なくとも1台成功した設備数。0台は切断判定または読取り可能アドレスなしを意味します。",
    },
    "K473": {
        "ja-JP": "現在のライセンス状態を表示。必要に応じて再アクティベーションやバックアップを行います。",
    },
    "K481": {"ja-JP": "データソース（ローカル収集 / リモート収集）と実行モードを選択します。"},
    "K483": {
        "ja-JP": "リモートモードはKanban.Collectorに接続し、複数画面で同一データソースを共有します。",
    },
    "K492": {
        "ja-JP": "表示モードは大画面ページのみを残し終了を制限します。工場画面向け（保存後再起動で有効）です。",
    },
    "K498": {"ja-JP": "通信アドレス、収集頻度、表示密度を設定します。"},
    "K500": {"ja-JP": "PLCブランドを選択し通信パラメータを設定します。"},
    "K511": {"ja-JP": "一括読取り、ポーリング、履歴書込みのペースを調整します。"},
    "K513": {"ja-JP": "バッチ内で許容する未設定アドレス数。0は完全連続のみ結合します。"},
    "K540": {"ja-JP": "生産シフトと時間範囲を管理します。"},
    "K541": {
        "ja-JP": "シフトは即時有効になり次回ポーリングから適用されます。現在のシフトが再判定される可能性があります。",
    },
    "Settings_Warn_PlcChanged": {
        "pt-BR": "Os parâmetros de conexão do PLC foram alterados. Ao salvar, a conexão atual será encerrada e restabelecida com os novos parâmetros.",
    },
    "Settings_Warn_DataModeChanged": {
        "pt-BR": "O modo de aquisição de dados foi alterado. Ao salvar, a fonte de dados será alternada (serviço local/remoto de coleta).",
    },
    "Settings_Warn_RunModeChanged": {
        "pt-BR": "O modo de execução foi alterado. Ao salvar, o modo será alterado (completo/somente visualização).",
    },
    "Settings_DailyReportMasterHint": {
        "pt-BR": "Em implantações com várias telas, habilite esta opção somente no nó mestre que deve gerar os relatórios diários; mantenha-a desativada nas outras telas para evitar PDFs duplicados.",
    },
}


def main() -> None:
    with CSV_PATH.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        rows: list[dict[str, str]] = []
        for row in reader:
            clean = {name: (row.get(name) or "") for name in FIELDNAMES}
            key = clean["Key"]
            if key in FIXES:
                for lang, value in FIXES[key].items():
                    clean[lang] = value
            rows.append(clean)

    with CSV_PATH.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=FIELDNAMES, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)

    print(f"Applied warning fixes to {CSV_PATH} ({len(rows)} rows)")


if __name__ == "__main__":
    main()
