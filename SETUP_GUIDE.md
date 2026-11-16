# Hướng dẫn Setup Scene, Prefab, Đồ họa iOS & Triển khai Server Docker

Tài liệu này hướng dẫn chạy dự án **Coral Archipelago (16 Modules hoàn chỉnh)** trong **Unity 2022.3 LTS / Unity 6 LTS (URP)**.

> **CÁCH NHANH NHẤT (1 CÚ CLICK):**
> 1. Mở Unity Hub -> **Add project from disk** -> chọn thư mục `d:\D\GameS` (`Packages/manifest.json` đã khai báo sẵn `URP`, `Netcode for GameObjects`, `Unity Transport`, `AI Navigation`, `uGUI`).
> 2. Đợi Unity tải Package xong, trên thanh menu chọn **`Coral Archipelago > One-Click Generate Scenes & Prefabs`**.
> 3. Script `Assets/_Project/Editor/CoralProjectAutoBuilder.cs` sẽ tự tạo toàn bộ Material, 4 Network Prefab, 2 UI Prefab, Đảo mẫu, Mặt biển, Trái Tim Đảo, Quái vật, và 2 Scene (`00_Bootstrap.unity`, `01_CoralWorld.unity`) được nối dây sẵn sàng.
> 4. Bấm **Play** ngay tại `00_Bootstrap` -> bấm **Start Local Host (Test)** để chơi thử!

---

## 1. Cài đặt Package cần thiết (Nếu dựng thủ công)
Mở `Window > Package Manager` (Registry: Unity Registry) và cài đặt:
1. **Universal RP (URP)** *(thường đã có sẵn nếu tạo dự án từ Template 3D URP)*.
2. **Netcode for GameObjects** (`com.unity.netcode.gameobjects`).
3. **Unity Transport** (`com.unity.transport`) *(tự động đi kèm NGO)*.
4. **Input System** (tuỳ chọn, code MVP dùng `UnityEngine.Input` & `Touch` tương thích sẵn để chạy ngay không cần cấu hình Input Action phức tạp).

---

## 2. Cấu hình Đồ họa iOS (Metal API + MetalFX + URP TBDR)

### A. Project Settings (`Edit > Project Settings`)
1. **Player > iOS Settings**:
   - **Auto Graphics API**: Bỏ tick, chỉ giữ **Metal**.
   - **Color Space**: `Linear` (bắt buộc để hiệu ứng phát quang bioluminescence + HDR Bloom đẹp).
   - **Target minimum iOS Version**: `15.0` trở lên (hỗ trợ đầy đủ MetalFX).
   - **Architecture**: `ARM64`.
2. **Quality & URP Asset**:
   - Tạo hoặc mở `URP-Mobile-High.asset` đang gắn trong `Project Settings > Graphics`:
   - **Rendering Path**: `Forward` (hoặc `Forward+` trên Unity 6 — tối ưu tốt nhất cho TBDR Apple Silicon).
   - **HDR**: Bật (`Checked`) để dùng HDR Bloom cho san hô phát sáng.
   - **Render Scale**: `0.75`.
   - **Upscaling Filter**: Chọn **MetalFX Spatial** (hoặc **MetalFX Temporal** nếu máy hỗ trợ).
   - **Shadows**: `Main Light` bật `Soft Shadows`, `Shadow Resolution = 1024` hoặc `2048`, `Max Distance = 55`, `Cascade Count = 2`.
   - **Post-Processing**: Trên Volume của Scene `01_CoralWorld`, bật **Bloom** (`Threshold = 1.1`, `Intensity = 0.65`, `Scatter = 0.6`). Các vật liệu san hô và pha lê dùng Shader Graph URP Lit nhân màu Emissive với biến toàn cục `_GlobalBioluminescence` (tự động cập nhật từ `WorldTimeManager`).

---

## 3. Tạo 2 Scene & Gắn Script

### Scene 1: `Assets/_Project/Scenes/00_Bootstrap.unity`
Thêm vào `File > Build Settings` ở **Index 0**.

Tạo các GameObject sau:
1. **`NetworkManager`** (Empty GameObject):
   - Add Component `NetworkManager`.
   - Add Component `UnityTransport` (kéo vào ô `Network Transport` của `NetworkManager`).
   - Trong `NetworkManager`:
     - Tick bật **`Connection Approval`**.
     - Tick bật **`Enable Scene Management`**.
     - Kéo Prefab `PlayerAvatar` vào ô `Player Prefab` (sau khi tạo ở bước dưới) và thêm các Prefab mạng vào `Network Prefabs List`.
2. **`ServerManagers`** (Empty GameObject):
   - Add Component `SplitSaveManager` (`Persistence/SplitSaveManager.cs`).
   - Add Component `ServerDormantController` (`Networking/ServerDormantController.cs`).
   - Add Component `NetworkBootstrap` (`Networking/NetworkBootstrap.cs`):
     - `Gameplay Scene Name`: `01_CoralWorld`
     - `Max Players`: `10`
     - `Default Port`: `7777`
3. **`iOSThermalManager`** (Empty GameObject):
   - Add Component `iOSGraphicsAndThermalManager` (`Client_iOS/iOSGraphicsAndThermalManager.cs`).
4. **`ClientLobbyCanvas`** (UI Canvas):
   - Thêm InputField nhập IP VPS + Nút `Connect` -> OnClick gọi `MobileTouchInputUI.OnClickConnectServer()`.
   - Thêm Nút `Host Test (Editor)` -> OnClick gọi `MobileTouchInputUI.OnClickStartHostTest()`.
   - Gắn `MobileTouchInputUI` lên Canvas này (hoặc tạo Canvas HUD chung giữ `DontDestroyOnLoad` tuỳ ý).

---

### Scene 2: `Assets/_Project/Scenes/01_CoralWorld.unity`
Thêm vào `File > Build Settings` ở **Index 1**.

1. **Môi trường Đảo & Biển**:
   - Tạo mặt nước biển (Plane lớn ở `Y = 0` với Shader nước URP trong suốt + phát sáng viền sóng).
   - Tạo địa hình các hòn đảo san hô nhô lên (`Y >= 1`), gán Layer/NavMesh Surface (nếu dùng `NavMeshAgent` cho quái vật, bấm Bake NavMesh).
   - Gắn `Directional Light` chính của Scene vào ô `Directional Sun Light` của `WorldTimeManager`.
2. **`WorldManagers`** (Empty GameObject — gắn `NetworkObject`!):
   - Add Component `NetworkObject`.
   - Add Component `WorldTimeManager` (`Core/WorldTimeManager.cs`) -> kéo Directional Light vào `Directional Sun Light`.
   - Add Component `PvEDirector` (`Gameplay/AI/PvEDirector.cs`).
   - Add Component `NetworkBuildingManager` (`Gameplay/Building/NetworkBuildingManager.cs`):
     - Trong mảng `Buildables`, khai báo 3 phần tử mẫu:
       1. `PrefabId = "coral_floor"`, `CostItemId = "coral_wood"`, `CostAmount = 2`
       2. `PrefabId = "glow_lamp"`, `CostItemId = "glow_crystal"`, `CostAmount = 2`
       3. `PrefabId = "shared_chest"`, `CostItemId = "coral_wood"`, `CostAmount = 4`
3. **Đặt Tài nguyên & Quái vật lên Đảo**:
   - **Cây Gỗ San Hô / Đá Pha Lê**:
     - Tạo Prefab có `NetworkObject`, `Collider`, và `HarvestableResourceNode`.
     - Đặt `Resource Item Id` = `"coral_wood"` hoặc `"glow_crystal"`.
     - Đặt rải rác trên đảo (script tự cấp `NodeUniqueId` duy nhất).
   - **Quái vật (Cua Đá Phát Quang)**:
     - Tạo Prefab có `NetworkObject`, `Collider`, `NavMeshAgent` (tuỳ chọn), và `ScalableMonsterAI`.
     - Đặt rải rác trên đảo (nhớ đăng ký vào `Network Prefabs List` trong `NetworkManager`).
4. **Tạo Canvas HUD Di động (`MobileHUD`)**:
   - Tạo Canvas (`Scale With Screen Size`: `1920x1080` hoặc `2532x1170`), đặt `MobileTouchInputUI` lên Canvas.
   - Kéo Image `JoystickBg` / `JoystickHandle` (bên trái màn hình) vào `joystickBackground` / `joystickHandle`.
   - Tạo các nút bên phải màn hình và nối `OnClick()`:
     - Nút **Attack / Harvest** -> `MobileTouchInputUI.OnClickAttackOrHarvest`
     - Nút **Build Floor** -> `MobileTouchInputUI.OnClickBuildFloor`
     - Nút **Build Lamp** -> `MobileTouchInputUI.OnClickBuildGlowLamp`
     - Nút **Build Chest** -> `MobileTouchInputUI.OnClickBuildSharedChest`
     - Nút **Deposit Wood** -> `MobileTouchInputUI.OnClickDepositWoodToNearestChest`
     - Nút **Withdraw Slot 0** -> `MobileTouchInputUI.OnClickWithdrawFirstFromNearestChest`

---

## 4. Tạo các Network Prefab bắt buộc

Tất cả các Prefab sau phải được kéo vào **`NetworkManager > Network Prefabs List`** (trừ `PlayerAvatar` kéo vào ô `Player Prefab` hoặc cả hai):
1. **`PlayerAvatar.prefab`**:
   - Components: `NetworkObject`, `NetworkTransform` (Sync Position & Rotation), `CharacterController`, `PlayerInventory`, `PlayerOnboardingController`, `NetworkPlayerController`.
2. **`PF_CoralFloor.prefab`** (`PrefabId = "coral_floor"`):
   - Components: `NetworkObject`, `BoxCollider`, Mesh sàn gỗ san hô.
3. **`PF_GlowLamp.prefab`** (`PrefabId = "glow_lamp"`):
   - Components: `NetworkObject`, `BoxCollider`, Mesh đèn pha lê phát sáng (dùng Emissive Material, không cần Point Light động!).
4. **`PF_SharedChest.prefab`** (`PrefabId = "shared_chest"`):
   - Components: `NetworkObject`, `BoxCollider`, `SharedChestInventory`.

---

## 5. Build & Chạy Dedicated Server bằng Docker trên Linux VPS

### Bước 1: Export Server từ Unity
1. Vào `File > Build Settings` (trên Unity 6 dùng **Dedicated Server > Linux**; trên Unity 2022.3 chuyển Platform sang **Dedicated Server**, Target **Linux**).
2. Tick các Scene: `00_Bootstrap`, `01_CoralWorld`.
3. Build ra thư mục: `d:\D\GameS\Build\LinuxServer\CoralServer.x86_64`.

### Bước 2: Đóng gói & Chạy 24/7 trên VPS
Upload thư mục dự án (hoặc chỉ `Build/LinuxServer/` + `Docker/`) lên Linux VPS, mở firewall cổng **7777/UDP**, rồi chạy:

```bash
cd Docker
docker compose up -d --build
```

### Bước 3: Kiểm tra hoạt động thực tế
- Xem log realtime để kiểm chứng Server tự vào **Dormant Mode (1Hz)** khi chưa có ai:
  ```bash
  docker logs -f coral_archipelago_server
  ```
- Mở app trên iPhone, nhập IP VPS và bấm **Connect** -> Server tự thức dậy **30Hz**, mở khóa `WorldTimeManager`, dữ liệu thế giới lưu tại `Docker/save_data/world_save.json` và từng người chơi lưu riêng tại `Docker/save_data/players/ios_xxx.json`.

---

## 6. Setup Shader Mặt nước & San hô Phát quang (Module 8 - URP / Metal FP16)

1. **Bật Depth Texture trong URP Asset** *(bắt buộc cho hiệu ứng bờ biển & độ sâu của mặt nước)*:
   - Mở `URP-Mobile-High.asset` -> tick bật **`Depth Texture`**.
2. **Tạo Material San hô / Đá Pha lê / Đèn Công trình**:
   - Tạo Material `MAT_CoralBiolum` dùng Shader **`CoralArchipelago/URP/CoralBioluminescentLit`**.
   - Bật **Enable GPU Instancing** ở cuối Inspector của Material để tối ưu vẽ hàng loạt san hô trên iPhone.
   - Chỉnh `_GlowColor` ở chế độ HDR (ví dụ màu xanh cyan `(0.1, 1.8, 2.5)` hoặc hồng san hô `(2.2, 0.3, 1.2)`). Khi trời chuyển tối (`18h–6h`), `WorldTimeManager` tự đẩy `_GlobalBioluminescence` lên `1.0`, kích hoạt mạch sáng nhấp nháy + viền Fresnel mà không cần đặt Point Light động.
3. **Tạo Material Mặt nước Biển Đêm**:
   - Tạo Material `MAT_CoralOcean` dùng Shader **`CoralArchipelago/URP/CoralOceanWaterURP`**.
   - Gắn lên Plane mặt biển lớn ở `Y = 0` trong Scene `01_CoralWorld` (đảm bảo Plane có nhiều lưới chia ô — ví dụ Plane 100x100 hoặc ProBuilder grid — để sóng Vertex nhấp nhô mượt mà).
4. **Gắn `BioluminescenceEnvironmentController`**:
   - Tạo GameObject `EnvironmentVisuals` trong `01_CoralWorld`, gắn `Client_iOS/BioluminescenceEnvironmentController.cs`.
   - Kéo Plane mặt biển vào **`Ocean Surface Transform`** để kích hoạt chu kỳ **Thủy triều (lên/xuống 2 lần/ngày)**.
   - (Tuỳ chọn) Gắn `ParticleSystem` bào tử phát sáng vào mảng **`Night Spore Systems`**; script sẽ tự giảm mật độ hạt khi `iOSGraphicsAndThermalManager` báo máy ấm lên.

---

## 7. Setup Giao diện Ô Lưới Túi đồ & Rương Chung (Module 9)

1. **Tạo Prefab Ô Đồ (`PF_InventorySlotButton.prefab`)**:
   - Tạo UI `Button` (kích thước `110x110` cho màn hình cảm ứng iPhone), bên trong có 2 `Text`: `ItemLabelText` và `AmountText` (góc dưới phải).
   - Gắn script `UI/InventorySlotButtonUI.cs` lên Button và kéo `Button`, `Image`, `ItemLabelText`, `AmountText` vào Inspector. Lưu thành Prefab.
2. **Dựng Modal trên `MobileHUD` của `01_CoralWorld`**:
   - Tạo Panel `InventoryModalRoot` (chứa nút Đóng `X`, nút `TransferModeButton`, 1 bảng lưới `PlayerGrid` gắn `GridLayoutGroup` 5 cột, và 1 bảng lưới `ChestGrid` gắn `GridLayoutGroup` 6 cột).
   - Gắn `UI/InventoryGridUI.cs` lên `MobileHUD` và gán các tham chiếu: `modalRoot`, `playerGridContainer`, `chestGridContainer`, `chestAvailablePanel`, `noChestHintObject`, `slotButtonPrefab`.
   - Nối `OnClick` của nút **TransferModeButton** -> `InventoryGridUI.OnClickCycleTransferMode()`.
   - Nối `OnClick` của nút **Đóng (X)** -> `InventoryGridUI.CloseModal()`.
   - Tạo nút **Bag / Chest** ngoài HUD chính -> `OnClick` gọi `MobileTouchInputUI.OnClickToggleInventoryModal()` (trên PC Editor có thể bấm phím **`I`** hoặc **`Tab`** để mở nhanh).

---

## 8. Setup Chế tạo Co-op, Tháo dỡ Công trình & Hang Pha lê Thủy triều (Module 10 & 11)

1. **Gắn `NetworkCraftingManager` lên `WorldManagers`**:
   - Trên GameObject `WorldManagers` (đã có `NetworkObject` ở Scene `01_CoralWorld`), Add Component `Gameplay/Crafting/NetworkCraftingManager.cs`.
   - Để trống danh sách `Recipes` để game tự nạp 4 công thức mặc định (`coral_pickaxe`, `biolum_spear`, `coral_tonic`, `refined_prism`) hoặc tuỳ biến thêm trong Inspector.
2. **Dựng Modal Chế tạo (`CraftingModalRoot`) trên `MobileHUD`**:
   - Tạo Panel `CraftingModalRoot` gồm: `RecipeListContainer` (gắn `VerticalLayoutGroup`), `NearbyChestHintText`, `FeedbackText`, và tạo Prefab `Button` đơn giản có 1 `Text` con.
   - Gắn `UI/CraftingPanelUI.cs` lên `MobileHUD`, nối `modalRoot`, `recipeListContainer`, `recipeButtonPrefab`, `nearbyChestHintText`, `feedbackText`.
   - Tạo nút **Craft** ngoài HUD chính -> `OnClick` gọi `MobileTouchInputUI.OnClickToggleCraftingModal()` (Editor phím **`C`**).
   - Tạo thêm 2 nút tiện ích ngoài HUD:
     - **Use Tonic** -> `MobileTouchInputUI.OnClickUseCoralTonic()`
     - **Demolish** -> `MobileTouchInputUI.OnClickDemolishNearestStructure()` (an toàn: tự chặn phá Rương Chung nếu trong rương còn vật phẩm của đồng đội).
3. **Đặt Hang Pha lê Thủy triều (`TidalCrystalCaveGate`)**:
   - Đặt 1 tảng đá/cửa hang tại lối vào hang động pha lê ngầm ở độ cao gần mực nước biển.
   - Gắn `Gameplay/Environment/TidalCrystalCaveGate.cs` và `Collider`. Cửa hang tự hạ xuống mở lối vào khung giờ thủy triều rút (**04:30–07:30** và **16:30–19:30** theo `WorldTimeManager`).

---

## 9. Setup Diagnostics & Simulator Test (Module 12)

1. Trên `MobileHUD` của `01_CoralWorld`, tạo góc nhỏ phía trên bên phải chứa 1 `Text` (`DiagnosticsText`) và 4 nút Test nhỏ (chỉ bật khi test Host/Editor):
   - Gắn `Client_iOS/DiagnosticsAndTestOverlayUI.cs`.
   - Nối các nút với:
     - **+1 Player Scale** -> `OnClickSimulateAddPlayer()`
     - **-1 Player Scale** -> `OnClickSimulateRemovePlayer()`
     - **Skip +3h** -> `OnClickSkipThreeWorldHours()` *(kiểm chứng ngay biển đêm phát quang, cửa hang thủy triều, và thời gian mọc lại mỏ tài nguyên)*
     - **Cycle Heat** -> `OnClickCycleSimulatedThermalLevel()` *(kiểm chứng tự giảm `RenderScale` & `ShadowDistance` khi iPhone nóng)*

---

## 10. Setup Hướng dẫn Tân thủ & Mũi tên Chỉ đường 3D (Module 13)

1. **Gắn `PlayerOnboardingController` lên `PlayerAvatar.prefab`**:
   - Component này quản lý 6 bước hướng dẫn cho từng người chơi độc lập (`TutorialStepIndex` và `TutorialCompleted` lưu trong `players/{uuid}.json`).
2. **Dựng Khung Hướng dẫn trên `MobileHUD` (`01_CoralWorld`)**:
   - Tạo Panel nhỏ phía trên bên trái (`TutorialPanelRoot`) gồm 3 `Text`: `StepTitleText`, `StepDescriptionText`, `StepProgressText`, và 1 nút **Bỏ qua** (`OnClick` -> `OnboardingTutorialUI.OnClickSkipTutorial()`).
   - Gắn `UI/OnboardingTutorialUI.cs` lên `MobileHUD` và kéo 4 tham chiếu trên vào Inspector.
3. **Mũi tên Chỉ đường 3D (`WorldWaypointArrow`)** *(tuỳ chọn nhưng rất trực quan trên iPhone)*:
   - Tạo một hình tam giác/mũi tên nhỏ hướng theo trục `+Z` (đặt vật liệu phát sáng `MAT_CoralBiolum`, **tắt Collider**).
   - Kéo GameObject mũi tên đó vào ô **`World Waypoint Arrow`** của `OnboardingTutorialUI` -> mũi tên tự động xoay quanh nhân vật chỉ thẳng tới cây Gỗ San Hô, mỏ Đá Pha Lê hoặc Rương Chung của đồng đội đã dựng sẵn trên đảo.

---

## 11. Tính năng Production & Co-op Mở rộng (Modules 14, 15, 16)

1. **Bảo mật Mã PIN Phòng (`SERVER_PASSCODE`) & Auto-Reconnect**:
   - Đổi mã PIN phòng trong `Docker/docker-compose.yml` (`SERVER_PASSCODE=1234`, hoặc để rỗng nếu mở phòng tự do). Trên màn hình `00_Bootstrap`, nhập đúng mã PIN vào `PasscodeInput`.
   - Khi iPhone chuyển mạng Wi-Fi <-> 4G/5G bị rớt kết nối ngoài ý muốn, `NetworkBootstrap` tự thử kết nối lại tối đa 3 lần (mỗi lần cách nhau 3s).
2. **Rolling Backup Thế giới (`backups/world_save_slot_0..4.json`)**:
   - `SplitSaveManager` tự sao lưu xoay vòng 5 bản lịch sử mỗi 30 phút và mỗi khi Server về trạng thái Dormant.
3. **Nhật ký Trại Chung, Trái Tim Đảo (Lv.1–5) & Lặn biển Oxy**:
   - `SharedCampLogManager` tự ghi lại lịch sử gửi/lấy rương, xây nhà, nâng cấp Trái Tim Đảo, và cho phép để lại ghi chú (`Post Note`) cho đồng đội ca sau.
   - `IslandCoreAltar` (Trái Tim Đảo) tỏa hào quang hồi phục HP/Stamina cho cả nhóm khi đứng gần trại và nâng cấp từ Lv.1 lên Lv.5 bằng `OnClickUpgradeIslandAltar()`.
   - `CurrentOxygen` tự giảm khi đầu nhân vật lặn dưới mực nước thủy triều (`WorldTimeManager.GetCurrentSeaLevelY()`).
4. **iOS Taptic Engine Haptics & Floating Damage Popups**:
   - `CombatFeedbackManager` + `iOSThermalPlugin.mm` tự rung Taptic Engine (`UIImpactFeedbackGenerator`) và hiện chữ sát thương/tài nguyên bay lên khi đánh quái, chặt cây san hô hoặc nâng cấp Trái Tim Đảo.




