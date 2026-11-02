# ディスク管理ツール（DiskManager）

実行ファイルのダウンロード.
https://github.com/k518-2026/DiskManager/releases

Windows 用のディスクユーティリティです。ドライブの健康診断、パーティションの作成と削除、
フォーマット（簡易／通常／ディープ）、安全な取り外しと再認識、古い CD / DVD の読み取り検査と
バックアップを 1 つのアプリにまとめています。

- WPF / C# / .NET 8
- Visual Studio 2022（17.8 以降）でそのままビルドできます

## ビルドと実行

1. `DiskManager.sln` を Visual Studio 2022 で開きます
2. 初回は NuGet パッケージ（`System.Management`、`System.Text.Encoding.CodePages`）が自動で復元されます
3. `F5` で実行します

コマンドラインの場合:

```
dotnet build DiskManager.sln -c Release
```

必要なワークロード: **.NET デスクトップ開発**

### 管理者権限について

`app.manifest` で `requireAdministrator` を指定しています。Visual Studio から `F5` で起動すると
UAC のため一度確認が出ます。デバッガーを使わずに実行する場合は `Ctrl+F5`、または
`bin\Release\net8.0-windows\DiskManager.exe` を右クリックして「管理者として実行」してください。

管理者権限がないと、S.M.A.R.T. の読み取り、パーティション操作、フォーマット、取り外しはすべて失敗します。
起動時に権限の有無をヘッダーに表示します。

## 画面の構成

| タブ | できること |
| --- | --- |
| 概要 | 選択したディスクの型番・シリアル・容量、パーティションを容量に比例した帯で表示、ボリュームごとの使用率 |
| 健康状態 | S.M.A.R.T. の全属性を読み出し、しきい値と突き合わせて良好／注意／不良を判定。温度・通電時間・代替処理済セクタなどを表示 |
| パーティション | 作成、削除、ドライブ文字の割り当て、MBR / GPT 初期化、オンライン切替、読み取り専用の解除、ディスク全体の消去 |
| フォーマット | 簡易／通常／ディープ（1〜7 回上書き）、ファイルシステムとクラスタサイズの指定、ラベル変更、空き領域だけの上書き。進み具合を % で表示。USB 接続（USB メモリ・ポータブル SSD / HDD）だけに絞った一覧と、複数ドライブの同時フォーマット |
| CD / DVD | メディアの内容確認、全セクタの読み取り検査、ISO イメージ作成、ファイル単位のバックアップ、トレイの開閉 |
| 取り外し・ツール | USB メモリなどの安全な取り外し、ハードウェアの再認識、diskpart スクリプトの直接実行 |

処理内容はすべて画面下部のログに残り、テキストファイルに保存できます。

## 実装のしくみ

| 機能 | 使っている仕組み |
| --- | --- |
| ディスク・ボリュームの列挙 | `Win32_DiskDrive` / `Win32_DiskPartition` / `Win32_LogicalDisk`、`MSFT_Disk` / `MSFT_PhysicalDisk` / `MSFT_Partition` |
| S.M.A.R.T. | `ROOT\WMI` の `MSStorageDriver_FailurePredictStatus` / `FailurePredictData` / `FailurePredictThresholds`。512 バイトの属性領域を 12 バイトずつ解析 |
| パーティション操作 | 記憶域 WMI のメソッド（`CreatePartition` / `DeleteObject` / `Initialize` / `Clear` / `Online` / `AddAccessPath`）。テキスト解析に頼らないため番号のずれが起きません |
| フォーマット | `format.com`（ディープフォーマットは `/P:n`）。ラベルは Unicode で確実に設定するため後から付け直します |
| 空き領域の上書き | `cipher /w` |
| 取り外し | `FSCTL_LOCK_VOLUME` → `FSCTL_DISMOUNT_VOLUME` → `IOCTL_STORAGE_EJECT_MEDIA` → `CM_Request_Device_Eject`（親デバイスを停止） |
| 再認識 | `CM_Reenumerate_DevNode` と `diskpart rescan` |
| 光学メディア | `\\.\X:` を直接開いて 2048 バイトセクタ単位で読み取り。ブロック読み取りに失敗したらセクタ単位に切り替えて再試行し、読めない部分はゼロで埋めて位置を保ちます |

## 安全のための制限

- Windows のシステムボリュームと起動パーティションは、削除・フォーマット・消去・取り外しの対象外です。C: は判定に関係なく常にフォーマットの対象外です
- USB 接続かどうかは、記憶域 WMI のバスの種類（BusType）でも判定します。UASP 接続のポータブル SSD は Win32_DiskDrive では SCSI に見えるためです
- フォーマットを途中で中止すると確認が出ます。中止したドライブは RAW 状態になるので、もう一度簡易フォーマットしてください

2026-11-02 に USBFORMATApp・UsbFormatter・DriveFormatterApp・USBEjectTool の機能をこのアプリに統合しました（元のソースは `E:\ClaudeCode\_archive\merged-2026-11-02\`）。
- データを失う操作の前に、対象名（例: `ディスク1`、`E`）の入力を求める確認画面が出ます
- diskpart スクリプトに `clean` / `delete` / `format` などが含まれる場合も同じ確認が入ります

## 注意点

- ディープフォーマットは SSD では代替ブロックまで消せません。廃棄前に確実を期すなら、メーカー製ツールの Secure Erase を併用してください
- USB 変換ブリッジ経由の外付け HDD や一部の NVMe は S.M.A.R.T. を公開しておらず、その場合は Windows の記憶域による健康状態のみ表示します
- 通常フォーマットとディープフォーマットは容量に応じて数十分〜数時間かかります。中止すると、そのボリュームは不完全な状態になります
