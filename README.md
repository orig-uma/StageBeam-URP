# StageBeam for URP

<!-- TODO: Documentation~/ にショーケースのスクリーンショット/GIF を置き、ここに貼る -->
<!-- 例: [![StageBeam Showcase](Documentation~/StageBeam_Showcase.png)](https://youtu.be/xxxx) -->

URP 向けのボリュメトリック・ステージビーム描画器。`ScriptableRendererFeature` が加算の光円錐を
レイマーチし、柔らかいエッジ・シーン深度遮蔽・ゴボ投影・解像度スケールで、コンサート／3Dライブの
ような「光の柱」を描きます。**灯数に依存しない共有ボリューム影**を核に、大量の灯体を影付き・白飛び
なしで描けるのが特徴です。URP 以外の依存はありません。

コンポーネント名: `Stage Beam Light`（手置き）／ `Stage Beam Renderer Feature`（描画）

## 特徴

Renderer Feature を1つ足して `Stage Beam Light` を置くだけで動きます。既定値のまま最低限の見た目が
成立し、影・ヘイズ・ゴボ・解像度などを段階的に積み増せます。データ駆動リグ（MVR/DMX/GDTF 等）からは
共通の `IStageBeamSource` 契約で同じレンダラーに供給できます。

* **解析的レイマーチのコーンビーム:** 単位コーンを解析交差＋N ステップ積分で描画。Field/Beam 角、
  Hotspot、軸方向減衰、Root Glare（光源フレア）、Henyey-Greenstein 異方性散乱（カメラを向いた
  ビームがフレア）。
* **灯数非依存の共有ボリューム影:** 全ビームが参照する世界空間の占有ボリュームを1つだけ構築するので、
  **影のコストが灯体数に依存しません**。遮蔽物の実メッシュをボクセル化（スキン姿勢・布変形込みの実
  シルエット）し、充填→平滑化→時間 EMA でチラつきのない影に。3 バックエンド（ScreenSpace / Volume /
  LightShadowMap）を切替。（→ [ARCHITECTURE](Documentation~/ARCHITECTURE.md)）
* **Soft Additive（白飛び対策）:** オフスクリーンに合計を蓄積して天井カーブに通すので、多灯が重なっても
  白飛び・ブルーム潰れ・ACES 変色が起きません。変調保存により飽和域でも影の削れ・ヘイズの流れが残ります。
  （→ [PERFORMANCE](Documentation~/PERFORMANCE.md)）
* **運用向けの負荷レバー:** 解像度スケール（Full/Half/Third/Quarter・深度考慮アップサンプル）、境界球
  カリング、シャドウ/ヘイズ間引き、ズーム適応ステップ、占有ビルドの更新間隔、静的/動的オクルーダー分離、
  任意の GPU インスタンシング。フィルレート律速なので、負荷は灯数よりも**画面被覆とビューポート解像度**で
  決まります。
* **ゴボ・床プール:** 1〜2 枚のゴボ投影（回転・アニメホイールスクロール独立）と、床への光プール
  （デカール投影、Receiver Layer マスク付き）。
* **ドロップイン & プログラマブル:** `Stage Beam Light` を置くだけの手置き運用と、`IStageBeamSource` を
  実装して外部リグから供給する運用の両対応。

## インストール

### Package Manager（Git URL）

`Window > Package Manager > + > Add package from git URL...` に以下を入力する。

```
https://github.com/orig-uma/StageBeam-URP.git
```

特定バージョンを指定する場合:

```
https://github.com/orig-uma/StageBeam-URP.git#v0.1.0
```

### Embedded

`Packages/com.origuma.stage-beam` に配置すると embedded package として認識される。

## 動作環境

* Unity 6 (6000.0) 以降
* Universal RP 17.0 以降
* Render Graph 有効（既定）

## 機能

| 項目 | 内容 |
| :--- | :--- |
| Cone Beam | 単位コーンを解析交差＋レイマーチ。Field/Beam 角、Hotspot、Axial Falloff、Root Glare（光源フレア） |
| Scattering | Henyey-Greenstein 異方性散乱（g）。カメラを向いたビームがステージヘイズのようにフレアする |
| Depth Occlusion | シーン深度でビームをクリップ。遮蔽面手前は Contact Fade で減衰（硬い明円盤を防ぐ） |
| Volumetric Shadows | 3 バックエンド（ScreenSpace / Volume / LightShadowMap）。**灯数非依存の共有占有ボリューム**。メッシュボクセル化（スキン/布込み実シルエット）＋充填→blur→時間 EMA。ゼロセットアップ（Renderer Feature のドロップダウン1つ） |
| Static/Dynamic Split | 動かない剛体オクルーダーをキャッシュ、動くもの（スキン/移動）だけ毎ビルド再構築。自動判定（レイヤー不要）。ゆるいマスクでもビルドスパイクを抑制。opt-in |
| Soft Additive | オフスクリーン蓄積＋天井カーブで多灯重なりの白飛び/ブルーム潰れ/ACES 変色を防止。変調保存で影・ヘイズを維持。床プールも同処理 |
| Gobo | 最大 2 枚のゴボ投影（Texture2DArray、回転・アニメホイールスクロール独立） |
| Resolution Scale | Full / Half / Third / Quarter（ピクセル 1/1・1/4・1/9・1/16）。深度考慮（joint bilateral）アップサンプル。フィルレート最大の負荷レバー |
| Anti-banding | 合成時（カメラの低仮数 HDR 形式に書く瞬間）の値相対ディザ。ビームの緩いランプが拾うマッハバンドを除去 |
| Haze Noise | 3D ノイズによるヘイズの揺らぎ（同梱・自動ロード）。流速一致ジッターで低解像度のグレインを緩和 |
| Surface Projection | 床のライトプール（ゴボ×色をデカール投影）。Receiver Layer マスク、Shadow Hardness で遮蔽感を調整 |
| Culling | 境界球による視錐台カリング＋画面投影半径カリング（遠く/画面外/極小を棄却） |
| GPU Instancing | ゴボ群ごとに `DrawMeshInstancedProcedural`。灯数が多く CPU/ドローコールがボトルネックの時に効く（opt-in、既定 OFF） |
| StageBeamLight | 手置きのドロップイン コンポーネント（MVR/DMX 知識不要、1 個 = 1 本） |
| IStageBeamSource | データ駆動リグ（MVR/DMX/GDTF 等）から同じレンダラーへ供給する契約 |
| One-click Setup | `Window > Origuma > Stage Beam Setup` で Renderer Feature を追加/削除。ドライバとマテリアルは実行時に自動生成 |

## 使い方

1. `Window > Origuma > Stage Beam Setup` で **Stage Beam Renderer Feature** を有効な URP レンダラーに追加する（`Stage Beam Light` のインスペクタからも可）
2. `GameObject > Stage Beam > Stage Beam Light` でビームを1本置く（既定値でそのまま光る）
3. 影が欲しければ Renderer Feature の **Shadows = Volume** に。**OccluderMask は演者レイヤーだけに絞る**と軽い

- **影モード・OccluderMask・静的/動的分離** → [USAGE](Documentation~/USAGE.md)
- **Soft Additive・解像度・GPU インスタンシング等の負荷調整** → [PERFORMANCE](Documentation~/PERFORMANCE.md)
- **内部構成・設計方針** → [ARCHITECTURE](Documentation~/ARCHITECTURE.md)

## ドキュメント

| ドキュメント | 内容 |
| :--- | :--- |
| [USAGE](Documentation~/USAGE.md) | 導入・Renderer Feature 設定・全パラメータ・影モード・負荷ガイド |
| [PERFORMANCE](Documentation~/PERFORMANCE.md) | Soft Additive 合成モデルと最適化手法（共有影/間引き/GPU インスタンシング/実測） |
| [ARCHITECTURE](Documentation~/ARCHITECTURE.md) | 内部構成・設計方針（レイヤ構成/コーンシェーダ/占有ボリューム） |

## 関連パッケージ

このパッケージは単体で完結しますが、上位に **MVR/GDTF リグ + DMX**（`com.origuma.mvr-toolkit`）、
**Art-Net / sACN I/O**（`com.origuma.dmx-toolkit`）、**Cue/Effect**（`com.origuma.show-control`）を
重ねると、実際の照明卓のショーを駆動できます。いずれも `IStageBeamSource` を実装して同じレンダラーに
供給する構成です。

## ライセンス

[MIT License](LICENSE.md)

## 作者

Origuma — [https://github.com/orig-uma](https://github.com/orig-uma)
