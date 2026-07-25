# StageBeam 技術アーキテクチャ

`com.origuma.stage-beam` の内部構造と描画パイプラインの技術解説。
導入手順・パラメータ一覧は [USAGE.md](USAGE.md) を参照。

## 1. レイヤ構成

パッケージは「データ供給」と「描画」を明確に分離した2層構造。

```
┌─ Driver 層 (Runtime/Driver) ────────────────────────────────┐
│  IStageBeamSource      ビーム供給者のインターフェース            │
│  StageBeamInstance     ビーム1本の全パラメータ (UnityEngine型のみ) │
│  StageBeamLight        手置き用ソース (MonoBehaviour, 1個=1本)   │
│  StageBeamDriver       全ソースを収集しキューへ変換               │
├─ Rendering 層 (Runtime/Rendering) ──────────────────────────┤
│  StageBeamQueue            フレームごとの {Matrix, MPB} キュー    │
│  StageBeamRendererFeature  URP RenderGraph パス群               │
│  StageBeamOcclusionBuilder 占有ボリューム構築 (plain class)      │
│  StageBeamOcclusionVolume  手動制御用オーバーライドコンポーネント   │
│  Resources/                シェーダ・compute・ノイズ (自動同梱)    │
└─────────────────────────────────────────────────────────────┘
```

外部パッケージ(mvr-toolkit の `StageBeamSource` 等)は `IStageBeamSource` を実装して
`StageBeamDriver.AddSource()` を呼ぶだけで同じレンダラーに乗る。依存は常に一方向
(上位 → stage-beam)。

## 2. フレームの流れ

1. **LateUpdate** — `StageBeamDriver` が登録済み全ソースの `CollectBeams()` を呼び、
   `StageBeamInstance` のリストを得る。各インスタンスをプールされた
   `MaterialPropertyBlock` に書き込み、共有コーンメッシュ・共有マテリアルと共に
   `StageBeamQueue` へ積む。マテリアルの複製は行わない。
2. **AddRenderPasses** (カメラごと) — `StageBeamRendererFeature` が
   シャドウモードのグローバルキーワード/変数を適用し、Volume モードなら
   占有ボリュームを構築(`Time.frameCount` ガードで1フレーム1回)。
   ヘイズノイズをグローバルにバインドしてパスを enqueue。
3. **RenderGraph 実行** — 下記パス群がキュー内容を描画する。

### RenderGraph パス構成

| パス | 条件 | 内容 |
|---|---|---|
| Stage Beams | 加算モード & フル解像度 | activeColor へ直接、各ビームを DrawMesh(加算) |
| Stage Beams (offscreen) | half-res **または** Soft Additive | オフスクリーン RT へレイマーチ |
| Stage Beams (composite) | 〃 | `StageBeamUpsample.shader` で深度考慮アップサンプル+**天井カーブ**合成 |
| Stage Beam Projection | `_projectOntoSurfaces` | 同じコーンを `StageBeamProjection.shader` (Cull Front) で描きゴボ×色を表面へデカール投影 |
| Stage Beam Decal Composite | 投影 & Soft Additive | デカール専用 RT の合計へ天井カーブを掛けて合成 |

**投影(床プール)の影**: プールにも `SampleBeamShadow` を掛けるが、ビームは Beer-Lambert で
遮蔽されても薄く残るため、床を完全遮蔽するとビームと矛盾する。透過率を
`saturate((shadow − h)/(1 − h))` でリマップし `_ProjShadowHardness`(既定 0.1)で
「残光を残しつつ床に遮蔽感」を出す中間点を取る(0=床が薄すぎ、大=ビームの残光と矛盾)。
より深い遮蔽は Volume ▸ Density を上げる(ビームと床の**両方**に等しく効き一貫性を保つ)。
`_ProjReceiverMask` で投影を受けるレイヤーを限定できる(キャラにゴボが焼き込まれるのを防ぐ)。

`_BeamRTParams` (逆ターゲットサイズ) をパスごとに設定し、シーン深度サンプリングの
UV がフル/半解像度どちらでも正しくなるようにしている。

**Soft Additive はフル解像度でもオフスクリーン経由になる** — 天井カーブは「重なった
ビームの合計値」を見る必要があり、これはパスごとの Blend 係数では表現できないため
(§ [PERFORMANCE.md](PERFORMANCE.md))。

## 3. コーンシェーダ (`Origuma/StageBeamCone`)

メッシュは描画領域を確保する bounding hull であり、実体はフラグメントでの
解析的レイマーチ。

- **交差**: 軸を +Z に取った CL frame で円錐台とレイの交差を解析的に解き
  (`BeamEntryDistance`: 側面 = apex まわりの無限円錐の2次方程式、端面 = 円盤)、
  区間 [tIn, tOut] を得る。カメラが内部なら tIn=0。
- **深度クリップ**: `_DepthOcclude` でシーン深度により tOut をクランプ。
  遮蔽面手前は `_SurfaceFadeDist` で密度をテーパー(contact fade)し、
  硬い明るい円盤の出現を防ぐ。
- **積分**: 区間を `_Steps` 等分し、サンプルごとに
  範囲内判定 × 側面フォールオフ × ホットスポット × 軸減衰 × ゴボ × ヘイズ ×
  HG 位相 × シャドウ を乗算して合算。`(Σ/steps)·chord·Density·Intensity` が線積分近似。
- **Root glare**: レンズ近傍の指数ブースト。超過分を `_RootWhite` で白成分/色成分に
  分離して別々に積分(強い光源コアの白飽和を再現)。
- **アンチバンディング**: IGN によるサンプルジッター + 低仮数ターゲット(R11G11B10)向けの
  値比例ディザ `_Dither`。**ジッターを時間方向にスクロール**させ隣接フレームで別パターンを
  踏ませる。スクロール速度は**ヘイズの流速に一致**(`_BeamJitterScroll` はワールドのヘイズ
  流速をスクリーン射影したもの)させ、ノイズがヘイズと一緒に流れて「画面に固定された
  汚れ」に見えにくくしている。低ステップ・低解像度時のグレインを TAA なしでも和らげる
  狙い(TAA があれば時間積分でさらに整うが、要否は好み・他の AA 次第)。
- **ブレンド**: `_BeamSrcBlend/_BeamDstBlend` で Additive (One One) と
  Soft Additive (OneMinusDstColor One) を切替(`StageBeamDriver.Blend`)。
- **ゴボ**: `Texture2DArray` ×2系統(回転・スライス独立、1系統目は UV スクロール=
  アニメーションホイール対応)。単発 Texture2D は `StageBeamLight` 側で
  `Graphics.CopyTexture` により1スライス配列へラップされる(結果はキャッシュ、
  失敗も null キャッシュして警告は1回のみ)。

### 座標系の契約

- `StageBeamInstance.Matrix` は「レンズ = 原点、ビーム軸 = ローカル **-Y**」
  (フィクスチャ規約: +Y が上)。`StageBeamLight.Axis.PositiveZ` は
  `FromToRotation(down, forward)` を行列に前掛けするだけで、シェーダ契約は不変。
- シェーダ内部は軸 +Z の CL frame に変換して計算(`(x, z, -y)` スィズル)。
  オブジェクトスケール 1 が前提。

## 4. ボリュメトリックシャドウ

`StageBeamShadow.hlsl` に3バックエンド。グローバルキーワード
`_STAGEBEAM_SHADOWS_SCREEN` / `_STAGEBEAM_SHADOWS_VOLUME` / `_STAGEBEAM_SHADOWS_LIGHT`
で選択し、無効時は `SampleBeamShadow()` が定数 1 に畳まれてコストゼロ。
**Volume バックエンドのシャドウデータは全ビーム共有**であり、コストはビーム本数に
依存しない — これが「大量灯体を影付きで描く」ための核心。

### ScreenSpace

サンプル→光源方向へ、近距離を細かい固定歩幅(`_BeamShadowStep`)でマーチし
シーン深度と比較。深度差に near 窓(バイアス直後フェードイン)と far 窓
(`_BeamShadowThickness` 超フェードアウト)を掛け、背景ジオメトリの誤遮蔽を防ぐ。
画面内の遮蔽物にはピクセル精度だが、画面外は見えない。

### Volume(占有ボリューム)

`StageBeamOcclusionBuilder` が毎フレーム実行する GPU パイプライン:

1. **発見**: レイヤーマスクで Renderer を列挙(`RescanInterval`=0.25s 間隔、
   edit mode 対応のため `realtimeSinceStartup` 基準)。ParticleSystemRenderer と
   `MaxOccluderSize` 超は除外。
2. **発見の非アクティブ対応**: レイヤーマスクの Renderer 列挙は**非アクティブも含めて**
   キャッシュし(サブメッシュ数まで)、gather/voxelize 時に `activeInHierarchy` でゲート。
   途中で有効化された遮蔽物が次フレームから即遮蔽に効く(再スキャン待ちなし)。
3. **メッシュボクセル化(既定 `MeshVoxelize`)**: 遮蔽物の**実メッシュ**を
   `StageBeamVoxelize.shader` で3軸(X/Y/Z)から直交投影ラスタライズし、各フラグメントが
   自分のワールド座標のボクセルへ UAV 書き込み(`RWTexture3D`)。シルエットが
   スキニング済みポーズ・布変形込みの実ジオメトリになる。
   - **フォールバック(`MeshVoxelize` off)**: 通常 Renderer は `localBounds` から
     有向ボックス、SkinnedMeshRenderer は骨格に球チェーンを配置(`BonesPerOccluder`)。
     旧来の近似で、低スペック向け。
   - **`StageBeamOccluderHint` は両モードで効く**: ヒントの付いた部分木は明示形状を
     compute で splat し、**ラスタライズ対象から外れる**。ドローコールが
     「Renderer 数 × 軸数」で増えるのはメッシュボクセル化だけなので、ヒントは
     負荷レバーでもある(踊り子の一団を数百ドローから 0 ドローへ)。代償は
     シルエット精度。形状数は `MaxOccluders` の予算内(人体1体で約17)。
4. **充填 → 平滑化 → 時間平均**(compute の ping-pong、順序が重要):
   - **GrowMax(純 max 膨張)×2**: メッシュボクセル化は三角形=**表面殻しか書かない**
     (中は空洞)ので、純 max で内側を 1.0 で埋める。純 max は中間値を作らない。
   - **Blur(中心重み平滑)×2**: 硬いバイナリ縁を連続グラデにする。max 膨張
     (`max(c, n·0.7)`)は 1.0→0.7→0.49 の**離散同心殻**を作り、シャドウレイで積分すると
     **縞状(terraced)の影の縁**が出る。blur なら内部 ~1 を保ち縁だけ連続減衰。GrowMax で
     中間レベルの無いバイナリにしてから blur するのが terracing を防ぐ鍵。
   - **TemporalBlend(時間 EMA)**: `history ← lerp(history, current, α)`。格子はワールド
     固定なので再投影不要。動く遮蔽物がボクセル間で snap する「ポッピング」を吸収し
     影の縁が滑らかに滑る(`TemporalSmoothing`)。
5. **フィット**: AutoFit で全遮蔽物の AABB + padding。**ボクセル格子はワールドに固定**
   (サイズを1m刻みに量子化+最小コーナーをボクセル単位にスナップ)し、被写体が
   動いても格子が動かない=再量子化によるシマー(チラつき)を防ぐ。
6. **バインド**: `_StageBeamOcc` / `_StageBeamOccMin` / `_StageBeamOccInvSize` ほかを
   グローバル設定。遮蔽物ゼロのフレームは Strength=0 を発行し、シェーダ側の
   マーチ自体をスキップさせる。**`VolumeUpdateInterval`** で毎フレームでなく N フレーム
   おきに再構築でき、構築コストを 1/N にできる(遮蔽物がゆっくりなら誤差は小さい)。

**実行タイミングの注意**: 上記 GPU パイプラインは `AddRenderPasses`(RenderGraph の
記録フェーズ)から `Graphics.ExecuteCommandBuffer` で即時実行する。RenderGraph の
正式パスとして記録するとレガシー一時RT(ボクセル化ダミー)がグラフ管理テクスチャと
メモリエイリアスしてビームバッファを破壊するため、あえてグラフの外で実行している。

### LightShadowMap(ハイブリッド)

ビームに実 URP スポットライトが割り当たっている場合、そのライトの**シャドウマップ**を
レイマーチ中に1タップ参照する(`AdditionalLightRealtimeShadow`)。シルエットは実
レンダリングジオメトリそのもの(指・髪・布)で、球/ボクセル近似もディザーノイズもない。
Renderer Feature が毎フレーム可視ライトから additional light index を解決して各ビームの
MPB に注入。**ライトが解決できないビームは自動的に Volume マーチへフォールバック**する
ハイブリッド構成なので、「ヒーロー数灯だけシャドウマップ、残り190灯は共有ボリューム」を
1モードで両立できる。実ライトは灯数分のシャドウマップ描画が要るため少数向け。

シェーダ側 `BeamShadow_Volume` は sample→light 線分を**占有ボックスへスラブ法で
クリップ**し、ステップ予算をその区間だけに配分する(光源が何 m 先でも影が届く)。
減衰は Beer-Lambert `trans *= exp(-occ·density·dt)` — `_BeamShadowDensity` は
「1m あたりの不透明度」なので、ステップ数・ボックスサイズに対して見た目が不変。
`_BeamShadowLightBias` は光源周りの除外半径で、フィクスチャ筐体による
ビーム根元の自己遮蔽を防ぐ。

### 所有権のルール

- 既定: Renderer Feature が自動でビルダーを所有(ゼロセットアップ)。
- シーンに有効な `StageBeamOcclusionVolume` があればそちらが排他的に所有
  (`Active` static)。Feature 側は引き継ぎフレームでグローバルを踏み潰さないよう
  `Release(neutraliseGlobals:false)` で退く。

### デバッグ

`_shadowDebug` (Volume モード): 赤 = 視線レイ上の占有、緑 = シャドウレイのヒット、
グレースケール = 実際に適用された減衰比 (sum/sumRaw)。
「ボリューム未バインド / レイ未達 / 強度未反映 / 知覚問題」を段階的に切り分ける。

## 5. リソース戦略

シェーダ4本・compute・ヘイズノイズ(HazeFBM3D)はすべて
`Runtime/Rendering/Resources/` に置き、`Shader.Find` / `Resources.Load` で解決する。
これによりプレイヤービルドへの同梱が自動化され、Always Included Shaders 等の
プロジェクト設定が不要になる。マテリアルとコーンメッシュはランタイム生成
(`StageBeamDriver.EnsureResources` / `CoreUtils.CreateEngineMaterial`)。

## 6. パフォーマンス特性

- **CPU**: ビーム1本あたり MPB 書き込みのみ(プール済み、GC ゼロ)。
  `Shader.PropertyToID` は全て static キャッシュ。
- **GPU 主コスト**: フラグメントのレイマーチ = 画面被覆 × `_Steps`。
- **シャドウ**: Volume はビーム本数非依存(共有ボリューム)。構築はボクセル数と
  遮蔽物三角形数に比例、サンプリングは「ビームのサンプル数 × ShadowSteps」の
  3D テクスチャタップ。
- **キーワード分岐**: シャドウ off ではシェーダバリアント自体にコードが乗らない。

### 実装済みの最適化(詳細は [PERFORMANCE.md](PERFORMANCE.md))

| 手法 | 効果 |
|---|---|
| 共有占有ボリューム | シャドウコストがビーム本数に非依存(大量灯体の核心) |
| ビーム境界球カリング | 視錐台外・画面投影半径 `_minScreenRadiusPx` 未満のビームをキュー段階で棄却 |
| 解像度スケール(Full/Half/Third/Quarter) | レイマーチ RT の解像度を落とす(ピクセル 1/1・1/4・1/9・1/16)。深度考慮アップサンプルで縁を保護。詳細は PERFORMANCE §3.7 |
| ゴボのプリフィルタ | ミップのフットプリントに**レイ方向のステップ間隔**を含める。自動ミップは画面方向しか見ないため、細いシャフトがステップ間で素通りしてモアレ化する。**負荷も同時に下がる**。PERFORMANCE §3.8 |
| バンディング対策 | ディザは**精度が失われる場所**=合成の出口(カメラの B10G11R11)に置く。蓄積は ARGBHalf なのでそこでは不要。PERFORMANCE §3.9 |
| `VolumeUpdateInterval` | 占有ボリュームを N フレームおきに再構築。構築コスト 1/N |
| シャドウ/ヘイズ項の間引き | 偶数ステップのみ再サンプル→奇数は再利用。支配的な 3D テクスチャタップを半減 |
| ズーム適応ステップ | ワイドズームはサンプル数を角度に反比例で削減(光束保存で暗く柔らかいため許容) |
| ワールド固定ボクセル格子 | 被写体移動でも格子不動=再量子化シマー(チラつき)を防止 |
| ズーム光束保存 | 立体角比 Ω(ref)/Ω(actual) で強度スケール。ワイドで薄まる物理的挙動+過剰塗りつぶし防止 |
| メッシュボクセル化 + GrowMax/blur/EMA | 球近似を捨て実ジオメトリ影。充填で影を濃く・blur で terracing 除去・EMA でポッピング吸収 |
| GPU インスタンシング(任意・既定 OFF) | ゴボ群ごとに1 `DrawMeshInstancedProcedural`。ドローコール(CPU)削減。フィルレートは不変なので効くのは CPU がボトルネックの時のみ。per-beam 値は頂点で読み flat varying で frag へ(per-pixel バッファ読み回避)。詳細は PERFORMANCE §3.11 |

負荷はフィルレート律速で、灯数よりも「画面被覆 × ビューポート解像度」で決まる。
計測時に固定すべき条件は PERFORMANCE §4 を参照。

## 7. 拡張ポイント

- **新しいビーム供給者**: `IStageBeamSource.CollectBeams(List<StageBeamInstance>)`
  を実装し、`StageBeamDriver.EnsureInstance().AddSource(this)` / `RemoveSource(this)`
  を OnEnable/OnDisable で呼ぶ。`StageBeamLight` が最小の参考実装。
- **ゴボホイール**: `StageBeamInstance.GoboArray(2)` にスライス済み
  `Texture2DArray` を渡せば、スライス切替でホイール回転を表現できる。
- **遮蔽の手動制御**: `StageBeamOcclusionVolume` をシーンに置くと、ボックス範囲・
  解像度・発見設定を Inspector から制御できる(自動ビルドは自動的に退く)。
