# Light Beam — 実装要件・設計定義 (Virtual Stage Lighting)

対象: バーチャル照明における **可視ビーム(volumetric)/ ゴボ投影** の描画系。
前提仕様: `com.origuma.gdtf` の [`LightBeamRequirements.md`](../../com.origuma.gdtf/Documentation~/LightBeamRequirements.md)(MVR/GDTF 機能要件, MoSCoW)。

> **注記(2026-07):** 本書は設計当時、これらが単一パッケージ `com.origuma.mvr-toolkit` に
> 同居していた前提で書かれている。その後パッケージを分割し、A層(データ)は
> `com.origuma.stage-rig`、B層(ビーム駆動)はその `Integrations/StageBeam`、C層(URP 描画)は
> 本パッケージへ移った。設計判断そのものは有効なので、層の役割分担として読むこと。
> 実際の配置は §付録 A を参照。
研究出典: Unity ゴボ照明最適化調査(縮小バッファ + レイマーチング + フロクセル・クラスタリング + テンポラル再構築)。

> この文書は **「何を作るか(要件)」と「どう作るか(設計)」** を定義する。
> 既存の機能要件(FR-*)は前提とし、本書は主に **§2 レンダリング表現(FR-R6 可視ビーム)** と
> **FR-D11 Gobo / FR-D12 Prism / FR-D13 Frost** を実体化するためのレンダリング・アーキテクチャを扱う。

---

## 0. 環境・前提

| 項目 | 値 | 影響 |
|----|----|----|
| Unity | 6000.4.9f1 (Unity 6) | RenderGraph API 必須(URP 17 は RenderGraph 既定) |
| Render Pipeline | URP 17.4.0 (HDRP 不使用) | カスタムは `ScriptableRendererFeature` + RenderGraph で実装 |
| ターゲットGPU | RTX 5060 / 5070 (Blackwell) | ALU 潤沢 / メモリ帯域がボトルネック → 縮小バッファ必須 |
| 目標 | 可視ビーム **100 灯** を実時間描画 | 単純フルスクリーン総当たりは不可。空間カリング必須 |
| 既存データ層 | `StageDmxRig`(GCゼロ) → `StageFixtureState` → `Apply` | **この層は不変**。描画層はここから状態を読むだけ |
| GDTF メッシュ | UniGLTF (`com.vrmc.gltf`) | GDTF の `.glb`/`.gltf` モデル取り込みに必須。任意依存で、`com.origuma.gdtf` の README 参照 |

### 0.1 既存資産との関係(現状の負債)

- `PC_Renderer.asset` に `StageLighting.StageBeamHalfResFeature`(`compositeMaterial`/`blurMaterial`)が
  登録されているが、対応スクリプト・マテリアルが存在しない **宙吊り参照**。
  → 本設計の Phase 1 着手時に **除去し、本設計の RendererFeature に置換**する(クリーンアップ TODO)。
- `StageFixtureBeam` は静的値(BeamType/Angle/Radius/Flux/CT)を保持済み。本設計はこれを描画入力として消費する。
- `StageFixtureState` は動的値(Dimmer/Color/Shutter/Zoom/Focus/Iris)を保持済み。Gobo/Prism/Frost は
  現状 `ChannelNorm[]` で値追跡のみ(FR-D16)。本設計で **state へ昇格**する(§4.2)。

---

## 1. 設計方針(研究レポートの取捨選択)

研究レポートは「数百万光源・AAA品質」を想定した上限技術を網羅している。本プロジェクトの現実的制約
(MVR ショー = 数十〜100灯、ミドルエンドPC、URP)に対し、**段階導入**で過剰投資を避ける。

| 研究レポート技術 | 採否 | 根拠 |
|----|----|----|
| 縮小バッファ(ハーフ解像度) | **採用(Must)** | 帯域ボトルネック対策の本丸。最大の費用対効果 |
| ローカル・ボリューメトリック(コーンメッシュをプロキシ描画) | **採用(Must)** | フルスクリーン起動を避ける。GDTF コーンと自然に一致 |
| GPU インスタンシング(`DrawMeshInstancedIndirect`) | **採用(Must)** | 100灯のドローコール/CPUオーバーヘッド排除 |
| ゴボ `Texture2DArray`(1サンプラ) | **採用(Should)** | FR-D11。状態変更コスト排除 |
| IGN ジッター + Early Ray Termination | **採用(Should)** | 低ステップでバンディング回避・無駄ループ削減 |
| ジョイント・バイラテラル・アップサンプル | **採用(Should)** | エッジ滲み防止。縮小バッファの前提条件 |
| フロクセル(Clustered)ライトカリング | **段階導入(Could→Should)** | 多数コーンが画面で重なる時のみ必要。Phase 3 |
| テンポラル再投影(モーションベクトル) | **段階導入(Could)** | ノイズ最終低減。ゴースト対策のコスト高。Phase 3+ |
| シャドウアトラス(100灯リアルタイム影) | **見送り(Won't / 別フェーズ)** | コスト極大。ビームはヘイズ内散乱が主目的で自己遮蔽影は副次的 |
| BVH ライト割当 | **見送り** | 100灯規模では過剰 |

### 1.1 推奨アーキテクチャ(結論)

> **「半解像度 + ローカルコーン・レイマーチング + GPUインスタンシング + バイラテラル合成」を中核に据え、
> フロクセルとテンポラルは Phase 3 のオプション最適化とする。**

理由: 100灯でも MVR ショーでは多くが空間的に分散し、画面内のコーン重なり(オーバードロー)が支配的になる
ケースは限定的。まずローカルコーン方式で「コーン外ピクセルを一切起動しない」だけで大半の予算を確保でき、
オーバードローが実測で問題化した段階で初めてフロクセル・カリングを足すのが投資効率上正しい。

---

## 2. レイヤ構成(全体アーキテクチャ)

```
┌─────────────────────────────────────────────────────────────────┐
│ A. データ層 (既存・不変)  com.origuma.stage-rig/Runtime/Dmx      │
│   StageDmxRig.Compute → StageFixtureState[]  (GCゼロ / Burst化可)     │
│   StageFixtureBeam (静的) + ChannelNorm[] (Gobo/Prism/Frost)    │
└───────────────┬─────────────────────────────────────────────────┘
                │ (NFR4: パイプライン非依存の抽象境界)
┌───────────────▼─────────────────────────────────────────────────┐
│ B. ビーム駆動層 (新規・パッケージ)  Runtime/Beam/                 │
│   IBeamRenderBackend                ← 抽象 (FR-I4 / NFR4)         │
│   BeamInstanceData (struct, GPU転送用フラット)                   │
│   BeamRegistry  : Fixture↔Beam の対応・有効灯リスト管理          │
│   BeamStatePacker : StageFixtureState+GdtfBeam → BeamInstanceData[] │
└───────────────┬─────────────────────────────────────────────────┘
                │ (StructuredBuffer<BeamInstanceData>)
┌───────────────▼─────────────────────────────────────────────────┐
│ C. URP レンダリング層  com.origuma.stage-beam/Runtime/           │
│   StageBeamRendererFeature : ScriptableRendererFeature           │
│     Pass1 DepthDownsample   (フル深度 → 半解像度, min/max)       │
│     Pass2 BeamRaymarch      (コーンインスタンス描画→半解像度RT)  │
│     Pass3 BilateralUpsample (半解像度→フル, 深度ガイド, 加算合成) │
│   Shaders: StageBeam.shader / BeamComposite.shader / *.hlsl      │
└─────────────────────────────────────────────────────────────────┘
```

**境界の意図(NFR4 / FR-I4)**: B 層は URP に依存しない純データ + 抽象 IF。`IBeamRenderBackend` 実装を
差し替えれば Built-in / HDRP / 簡易フォールバックへ移植できる。Light を持たない Prefab でも B 層が
`BeamInstanceData` を生成するため自前描画が成立する(FR-I4)。

---

## 3. データ設計

### 3.1 `BeamInstanceData`(GPU 転送 struct / 1灯1要素)

レイマーチングシェーダーが 1 サンプラ・1 バッファで全灯を評価できるフラット表現。`StructuredBuffer` に詰める。

```hlsl
struct BeamInstanceData
{
    float3 originWS;      // ビーム原点(レンズ位置, StageFixtureBeam 変換後 Position)
    float  startRadius;   // 始端半径 m (= BeamRadius, Iris で縮小)
    float3 dirWS;         // ビーム方向(Beam ローカル -Y, Pan/Tilt 反映後)
    float  range;         // 到達距離 m (LOD/設定)
    float3 color;         // 線形 RGB(Dimmer×Shutter×色合成済み or 別途 intensity)
    float  intensity;     // 最終可視強度 (Dimmer×ShutterOpen×LightIntensityScale)
    float  cosHalfField;  // cos(FieldAngle/2)  外縁(到達端)
    float  cosHalfBeam;   // cos(BeamAngle/2)   内縁(ホットスポット)→ ソフトエッジ補間に使用
    float  edgeSoftness;  // BeamType 由来(Wash>Spot)+ Frost 量
    int    goboSlice;     // Texture2DArray スライス番号(-1 = ゴボなし)
    float  goboRotation;  // ラジアン(Gobo1Pos)
    float2 _pad;
    float4x4 worldToBeam; // ワールド→ビーム射影空間(ゴボ UV 算出用, §5.3)
};
```

設計判断:
- **色と強度を分離**しておく(`color` は正規化色、`intensity` で乗算)。HDR 加算合成で扱いやすく、
  Strobe の 0/1 を `intensity` に畳み込める(FR-D14)。
- `cosHalfBeam`/`cosHalfField` を **cos で事前計算**しておき、シェーダーでの `acos` を回避(ALU 節約)。
- 矩形ビーム(FR-R3 Rectangle)は v1 スコープ外。将来 `float4x4 worldToBeam` の射影と UV マスクで対応可能。

### 3.2 LOD / 有効灯フィルタ(NFR2)

`BeamRegistry` が毎フレーム以下で **描画対象灯を間引く**:
1. `intensity <= ε`(消灯)→ インスタンスから除外。
2. カメラ距離 / 画面占有でソートし上限 `MaxVisibleBeams`(既定 64, 設定可)で打ち切り。
3. 距離 LOD で `range` とレイステップ数を段階制御(遠灯 = 短レンジ・低ステップ)。

→ `DrawMeshInstancedIndirect` の引数バッファに有効件数のみ書き込む。

---

## 4. データ層からの値マッピング(B層: BeamStatePacker)

### 4.1 既存 state からの算出(FR-D1〜D8, FR-D14, FR-D15)

| BeamInstanceData | 算出元 | 備考 |
|----|----|----|
| `originWS` | Beam ジオメトリの変換後 `Position` | FR-S9。他面に塞がれない原点 |
| `dirWS` | Beam transform の -Y(Pan/Tilt 反映後) | FR-D1 / AC7 |
| `intensity` | `Dimmer × ShutterOpen × LightIntensityScale` | FR-D14。Strobe は ShutterOpen に畳込済 |
| `color` | `StageFixtureState.Color`(線形化) | FR-D8 / FR-P4(0..1クランプ済) |
| `cosHalfField` | `Zoom>0 ? Zoom : FieldAngle` の半角 cos | FR-D5 / FR-D15(物理範囲補間済) |
| `cosHalfBeam` | `BeamAngle`(Zoom 比率連動)の半角 cos | FR-R1/R2 ソフトエッジ |
| `startRadius` | `BeamRadius × lerp(irisMin,1,Iris)` | FR-S3 / FR-D6 |
| `edgeSoftness` | BeamType(Wash/Spot)基準 + `Frost` 加算 | FR-R7 / FR-D13 |
| `goboSlice` | Gobo1 channel → スライス LUT | FR-D11(§4.2 で state 昇格) |
| `goboRotation` | Gobo1Pos channel(物理 deg→rad) | FR-D11 |

### 4.2 `StageFixtureState` への追加(Gobo/Prism/Frost の昇格)

現状 `ChannelNorm[]` 追跡のみの属性を、描画に使うため struct へ昇格(GCゼロ維持・値型のまま):

```csharp
// StageFixtureState へ追加
public int   GoboSlice;     // -1 = none(ColorWheel/Gobo の slot index 解決後)
public float GoboRotation;  // degrees
public float Frost;         // 0..1
public int   PrismFacets;   // 0 = off(Phase 3+)
public float PrismRotation; // degrees
```

`StageDmxRig.Compute` の switch に `case StageAttribute.Gobo1 / Gobo1Pos / Frost1 / Prism1 / Prism1Pos` を追加。
ColorWheel/Gobo の **slot→slice/color の解決テーブルは bake 時に構築**(`GdtfRigBaker`)し、実行時は
インデックス参照のみ(NFR1 GCゼロ維持)。FR-D16(未対応属性の値追跡)は従来どおり維持。

> 注: §1 の研究レポートが言う「ゴボ Texture2DArray」は、この `GoboSlice` を Z スライスとして
> 1 サンプラで引く構造に直結する。ベイク時に GDTF 内のゴボ画像を `Texture2DArray` へ集約する。

---

## 5. URP レンダリング層(C層)の設計

### 5.1 RenderGraph パス順序(FR-I2 と整合)

不透明描画後・半透明/ポストプロセス前に挿入(`RenderPassEvent.AfterRenderingOpaques` 近傍)。

| # | Pass | 入力 | 出力 | 解像度 |
|---|----|----|----|----|
| 1 | **DepthDownsample** | `_CameraDepthTexture` | `_BeamHalfDepth` | 1/2(設定で 1/4) |
| 2 | **BeamRaymarch** | HalfDepth, `StructuredBuffer<BeamInstanceData>`, GoboArray, BlueNoise | `_BeamHalfColor`(RGBA16F) | 半解像度 |
| 3 | **BilateralUpsample + Composite** | HalfColor, フル `_CameraDepthTexture` | カメラカラー(加算) | フル |

- 深度ダウンサンプルは **min/max 選択**(単純バイリニア不可)でシルエット破綻を防ぐ(研究レポート §4)。
- Pass2 の RT は **HDR(R11G11B10F or RGBA16F)** 必須(加算ビームの飽和回避)。

### 5.2 Pass2 レイマーチング(コーン・プロキシ方式)

1. **コーンメッシュを `DrawMeshInstancedIndirect`** で全有効灯分描画(GPU 主導, ドローコール 1)。
   各インスタンスは `BeamInstanceData` を `SV_InstanceID` で参照。
2. **フラグメントで視線レイを構築**し、コーン区間 ∩ シーン深度(HalfDepth)でレイ始終点を決定。
3. **Early-Z + ローカル限定**: コーン外ピクセルは起動しない(フィルレート温存, 研究 §早期終了)。
4. レイを `N` ステップ(LODで 16〜32)で進め、各ステップ:
   - 点が円錐内か(`dot(normalize(p-origin),dir) > cosHalfField`)判定。
   - 角度フォールオフ: `cosHalfBeam..cosHalfField` で `smoothstep`(BeamType ソフトエッジ, FR-R1/R2/R7)。
   - 距離減衰(Beer-Lambert 風 + 逆二乗の簡易合成)。
   - ゴボ有効時、`worldToBeam` で UV 算出 → `Texture2DArray.SampleLevel(goboSlice)`(§5.3)。
   - In-scatter を `intensity × color × falloff × gobo × phase` で加算。
5. **IGN/BlueNoise ジッター**でレイ開始位置をピクセル毎にオフセット(バンディング→高周波ノイズ化)。
6. **Early Ray Termination**: 透過率 < 0.01 で `break`。

> 単一コーンのみ評価する素朴版でも、画面内でコーンが重なる領域は加算ブレンドで自然に合成される。
> 重なりが実測で重い場合のみ Phase 3 のフロクセルで「1レイが見るべき灯」を間引く。

### 5.3 ゴボ UV(研究レポートの射影変換)

ワールド点 `p` を `worldToBeam`(= ライトのビュー×射影)で変換 → 透視除算(/w)→ NDC(-1..1)
→ `uv = ndc.xy*0.5+0.5` → 回転(`goboRotation`)→ `Texture2DArray.Sample(sampler, float3(uv, goboSlice))`。
Focus(FR-D7)で mip/ぼかし量、Frost(FR-D13)でサンプル平均化(ソフト化)。

### 5.4 BeamType 分岐(FR-R1〜R5)

| BeamType | 描画 |
|----|----|
| Wash / Fresnel / PC | コーン・レイマーチ。`edgeSoftness` 大(柔エッジ) |
| Spot | コーン・レイマーチ。`edgeSoftness` 小(硬エッジ)+ ゴボ有効 |
| Rectangle | **Phase 3+**。角錐プロキシ + 矩形 UV マスク(ThrowRatio/RectangleRatio) |
| None / Glow | **ビーム非描画**。ジオメトリ自己発光のみ(既存 ColorApplier 経路) |

---

## 6. フェーズ計画(MoSCoW 連動)

| Phase | 内容 | 対応要件 | 完了基準(AC) |
|----|----|----|----|
| **P0** | 既存: Light 駆動(Spot inner/outer, Zoom, Dimmer, Strobe, Color) | FR-R5, D1-D5, D14, D15 | AC1-AC4(済) |
| **P1** | 宙吊り `StageBeamHalfResFeature` 除去 → 新 RendererFeature 雛形 + **単純加算コーン**(レイマーチ無し, ソフトエッジ円錐)半解像度+バイラテラル合成 | FR-R6, NFR2, NFR4 | ヘイズ無しでも可視ビームが見え、Dimmer/Color/Zoom 追従 |
| **P2** | コーン・**レイマーチング** + IGN ジッター + Early Termination + **ゴボ Texture2DArray** + Frost/Focus | FR-R1/R2/R7, D6/D7/D11/D13 | ゴボ形状が空間に投影、BeamType でエッジ差、Iris 反映(AC5 含む) |
| **P3** | **フロクセル・ライトカリング**(オーバードロー対策)+ 距離LODステップ + (任意)テンポラル再投影 | NFR2 強化, 100灯目標 | RTX 5060 で 100灯 60fps 維持 |
| **P4** | Rectangle ビーム / Prism 複製 / ColorWheel slot 厳密化 | FR-R3, D9, D12 | — |

> v1(P1+P2)で「見える・操作に追従する」を達成。100灯スケール(P3)は実測ドリブンで最適化を足す。

---

## 7. 性能予算(目安, RTX 5060 / 1080p)

| 項目 | 予算 |
|----|----|
| 解像度 | 半解像度(540p 相当)で raymarch |
| レイステップ | 近灯 32 / 遠灯 16(LOD) |
| 有効灯上限 | `MaxVisibleBeams` 既定 64(100灯中、画面外/消灯を除外後) |
| ゴボフェッチ | 1 サンプラ(Texture2DArray) |
| ドローコール | コーン全灯 1(`DrawMeshInstancedIndirect`) |
| 合成 | バイラテラル 1 パス(深度ガイド) |
| GC | 毎フレーム **0 バイト**(NFR1。バッファは事前確保・`SetData` のみ) |

ボトルネック予測(研究 §ハードウェア): ALU でなく **テクスチャ random access の帯域**。
→ ステップ数より「半解像度」と「コーン外不起動」が効く。フロクセルは帯域の二次最適化。

---

## 8. 非機能・テスト

- **NFR1(GCゼロ)**: B層の `BeamInstanceData[]` とネイティブ/管理バッファは bake 時確保。毎フレーム `SetData` のみ。
- **NFR3(Burst化可)**: `BeamStatePacker` は struct 配列の純変換に保ち、後に `IJobParallelFor` 化可能に。
- **NFR4(PL非依存)**: B層は URP 型を参照しない。`IBeamRenderBackend` のみが C 層へ橋渡し。
- **FR-I5(モニタ)**: `StageFixtureMonitorWindow` に Gobo/Frost/Prism/有効灯フラグを追加表示。
- テスト(EditMode): `BeamStatePacker` の値マッピング(Zoom→cosHalfField, Dimmer×Shutter→intensity,
  Gobo slot→slice)を `StageFixtureState` 入力に対して検証(既存 `StageDmxRuntimeTests` に追補)。
- 受け入れ: AC5(None/Glow 非描画)、AC6(未対応属性のモニタ追従)に加え、
  **AC-B1**: ゴボ slot 変更で投影パターンが切替。**AC-B2**: Zoom 連続変化でコーン角が連続変化。

---

## 9. 決定が必要な事項(Open Questions)

1. **ヘイズ/フォグ密度の供給源**: シーン共通の一様密度(URP Volume パラメータ)で開始し、将来 3D 密度
   テクスチャへ拡張するか。→ v1 は一様密度を推奨。
2. **MaxVisibleBeams の既定値**と LOD 距離: 実 MVR ショー(`Demoshow_grandMA3`)で実測して調整。
3. ~~**C層の置き場所**~~: **決定済み(2026-07)**。プロジェクト固有の `Assets/LightBeam` ではなく、
   独立パッケージ `com.origuma.stage-beam` になった。ソース非依存の描画器として単体でも使えるので、
   プロジェクトに縛る理由がなかった。
4. **ゴボ画像の出所**: GDTF 内蔵ゴボ画像をベイク時に `Texture2DArray` 集約する実装が必要(別タスク)。

---

## 付録 A: ファイル構成(実装後の実配置)

設計時は単一パッケージ + `Assets/` 配下を想定していたが、実装ではパッケージを分割し、
C層は独立パッケージになった。層の役割は設計どおり。

```
com.origuma.stage-rig/Integrations/StageBeam/     (B層: 描画器へのアダプタ)
  StageBeamSource.cs          IStageBeamSource 実装。灯体状態 → ビーム
  BeamInstanceData.cs         GPU 転送用のフラットな struct
  BeamStatePacker.cs          StageFixtureState + StageBeamParams → BeamInstanceData
  StageGoboArray.cs           灯体型ごとの Texture2DArray(ゴボ)
  StageBeamLens.cs            レンズ/発光面の見え
  StageLensEmissiveSync.cs    レンズ発光の同期
  StageBeamLightSync.cs       実 Unity Light のプーリング(面照射・影)
  StageBeamAutoBootstrap.cs   描画器の自動生成

com.origuma.stage-beam/Runtime/                   (C層: URP 描画。本パッケージ)
  Driver/IStageBeamSource.cs      抽象境界(NFR4)。B層はこれだけを実装する
  Driver/StageBeamDriver.cs       登録されたソースから毎フレーム収集
  Driver/StageBeamInstance.cs     描画単位
  Driver/StageBeamLight.cs        ソース非依存の単体ビーム
  Rendering/StageBeamRendererFeature.cs   RenderGraph パス一式
  Rendering/StageBeamInstancing.cs        インスタンシング
  Rendering/StageBeamOcclusion*.cs        占有ボリュームによる影
  Rendering/Resources/StageBeamCone.shader        コーン raymarch
  Rendering/Resources/StageBeamConeCore.hlsl      cone/falloff/gobo/jitter
  Rendering/Resources/StageBeamUpsample.shader    縮小バッファの合成
  Rendering/Resources/StageBeamProjection.shader  床面へのゴボ投影
  Rendering/Resources/StageBeamVoxelize.shader    遮蔽ボリューム生成
  Rendering/Resources/StageBeamShadow.hlsl        影のサンプリング
```

B層が `com.origuma.stage-rig` 側に居るのは、依存を「上位 → 描画器」の一方向に保つため。
C層は誰がビームを供給するかを知らず、`IStageBeamSource` の登録を受けるだけ。

## 付録 B: 研究レポート技術 → 本設計マッピング早見

| 研究レポート | 本設計 |
|----|----|
| 縮小バッファ(ハーフ) | §5.1 Pass1/2 半解像度 |
| Interleaved/IGN ジッター | §5.2-5 |
| ジョイント・バイラテラル | §5.1 Pass3 |
| フロクセル(Clustered) | §6 P3 |
| テンポラル再投影 | §6 P3(任意) |
| Texture2DArray ゴボ | §4.2 / §5.3 |
| DrawMeshInstancedIndirect | §3.2 / §5.2 |
| Early Ray Termination / 深度バウンズ | §5.2 |
```
