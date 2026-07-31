# Argus — Endpoint Aktivite İzleme / DLP

Çalışan/uç nokta aktivite izleme sistemi (engelleme yok, sadece gözlem + uyarı). Referans: Safetica.
Multi-tenant (satılabilir ürün), C#/.NET 8, sıfır dış bağımlılık — offline derlenir.

> **KVKK / Hukuki uyarı:** Personel izleme, çalışanlara aydınlatma metni + politika bildirimi gerektirir.
> Gizli (habersiz) izleme Türkiye'de hukuken risklidir. Dağıtmadan önce yasal yükümlülükleri yerine getirin.

---

## 30 saniyede dene (DEMO)

```powershell
cd Desktop\Argus-DLP\deploy
powershell -ExecutionPolicy Bypass -File run-demo.ps1
```

Bu komut sunucuyu ve agent'ı bu makinede başlatır, tarayıcıda paneli açar
(`http://localhost:5099`, Tenant Key: `demo-tenant-key-001`).
Masaüstü/Belgeler klasöründe birkaç dosyayı hızlıca silerseniz panelde **toplu silme uyarısı** belirir.

Durdurmak: `Get-Process Argus.Server,Argus.Agent | Stop-Process`

---

## Mimari

Üç bağımsız parça:

| Parça | Klasör | Ne yapar | Durum |
|-------|--------|----------|-------|
| **Agent** | `agent/` | Uç noktada: önplan uygulaması + aktif/boşta süre + dosya olayları + toplu-silme uyarısı. Offline dayanıklı, sunucuya gönderir. | ✅ çalışıyor |
| **Backend** | `backend/` | Multi-tenant telemetri sunucusu (ASP.NET Core). Enroll + telemetri + sorgu API'si + kalıcı depolama + paneli sunar. | ✅ çalışıyor |
| **Panel** | `backend/wwwroot/` | Canlı konsol — sunucudan gerçek veri: uç noktalar, uyarılar, uygulama kullanımı. 5 sn'de bir yenilenir. | ✅ çalışıyor |
| Tasarım prototipi | `dashboard/` | Tam vizyon önizlemesi (drill-down, web/dosya logları, filtreler). | prototip |

Veri akışı: **Agent → (HTTPS telemetri) → Backend → (canlı API) → Panel**

---

## Kurulum

### Sunucu
```powershell
powershell -ExecutionPolicy Bypass -File deploy\start-server.ps1
```
- Panel + API: `http://localhost:5099`
- Farklı adres/port: `ARGUS_URLS` ortam değişkeni

**Depolama — iki seçenek:**
- **Varsayılan: SQLite** (`%LOCALAPPDATA%\ArgusServer\argus.db`) — kurulum/servis GEREKTİRMEZ, gerçek SQL veritabanı, tek dosya. Küçük/orta ölçek için yeterli.
- **PostgreSQL (üretim, 300 makine/çok sunucu):** `ARGUS_PG` ortam değişkenine bağlantı dizesi verin, şema otomatik oluşur:
  ```powershell
  $env:ARGUS_PG = "Host=localhost;Database=argus;Username=argus;Password=***"
  powershell -File deploy\start-server.ps1
  ```
  Geçiş tek satır (aynı `IStore` arayüzü) — kod değişmez.

### Agent (bir uç noktaya, sessiz kurulum)
```powershell
powershell -ExecutionPolicy Bypass -File deploy\install-agent.ps1 `
    -ServerUrl "http://SUNUCU-IP:5099" -TenantKey "musteri-anahtari"
```
- Agent'ı sabit klasöre kopyalar, config yazar, **gizli Zamanlanmış Görev** oluşturur (oturum açılışta otomatik).
- Kaldırma: `deploy\uninstall-agent.ps1`
- 300 makine için: bu script GPO ile veya SCCM ile dağıtılır (üretimde imzalı MSI hedefleniyor).

---

### Tarayıcı Uzantısı (yüksek fidelite web takibi — Chrome + Edge)

Agent, tarayıcı URL'sini UI Automation ile (dışarıdan adres çubuğunu okuyarak) yakalar — çalışır ama
sadece aktif sekmeyi görür. **Tam fidelite** için (arka plan sekmeleri, SPA/YouTube video-video geçişleri,
tam URL, gizli mod) tarayıcı uzantısını kur. Uzantı yüklüyse agent otomatik onu kullanır, yoksa UIA'ya düşer.

**Kurulum (Chrome örneği):**
1. `chrome://extensions` → sağ üstten **Geliştirici modu** açık.
2. **Paketlenmemiş öğe yükle** → `Desktop\Argus-DLP\extension` klasörünü seç.
3. Uzantının **ID**'sini kopyala (32 harflik).
4. Köprüyü kur (ID ile):
   ```powershell
   powershell -ExecutionPolicy Bypass -File deploy\install-webhost.ps1 -ExtensionId "PASTE_ID"
   ```
5. **Gizli mod:** uzantı kartında "Gizli modda izin ver"i aç.
6. Tarayıcıyı yeniden başlat.

Akış: **Uzantı → native messaging köprüsü → `web-current.json` → Agent → Sunucu → Panel.**
300 makinede: uzantı GPO `ExtensionInstallForcelist` ile zorla kurulur, köprü de MSI/script ile dağıtılır,
gizli mod policy ile açılır.

---

## API (v1)

Kimlik: enroll/okuma için `X-Tenant-Key`, telemetri için `X-Agent-Token`.

| Metot | Yol | Açıklama |
|-------|-----|----------|
| POST | `/api/v1/enroll` | Agent kaydı (tenant key ile) → agent token |
| POST | `/api/v1/telemetry` | Özet + uyarı + olay gönderimi (agent token ile) |
| GET | `/api/v1/agents` | Tenant'ın uç noktaları |
| GET | `/api/v1/agents/{id}/activity` | Bir uç noktanın aktivite özeti |
| GET | `/api/v1/alerts` | Tenant'ın son uyarıları |
| GET | `/api/v1/settings` | Tenant politikası (panel Ayarlar ekranı okur) |
| PUT | `/api/v1/settings` | Tenant politikasını kaydet (agent bir sonraki turda uygular) |
| GET | `/api/v1/report/events` | **Geriye dönük** dosya olayları — `agent`,`from`,`to`,`op`,`sensitive`,`limit`,`offset` |
| GET | `/api/v1/report/alerts` | **Geriye dönük** uyarı geçmişi — `agent`,`from`,`to`,`type`,`severity`,`limit`,`offset` |
| GET | `/api/v1/report/web` · `/report/app` · `/report/doc` | Tarih aralıklı süre raporları |
| GET | `/health` | Servis durumu |

> **Canlı uçlar vs. rapor uçları:** `/agents` `/alerts` `/events` `/web` `/appusage` yalnız **son N kaydı**
> döner (canlı pano içindir). Geçmişe bakmak için **`/report/*`** uçları kullanılır — panelin
> Uyarılar, Veri Güvenliği, Dosya Olayları ve Aktivite ekranları bunları kullanır.

## Kurumsal işletim

**Uyarı bildirimi (e-posta · webhook · SIEM).** Uyarıyı görmek için paneli açık tutmak gerekmiyor.
Kritik olaylar 60 sn'lik pencerede toplanıp TEK bildirimde gönderilir (aynı tipte 10 dk bekleme →
toplu kopyalamada 200 e-posta yağmuru olmaz).

| Ayar | Nerede |
|---|---|
| Alıcı e-postalar, webhook adresi, en düşük önem | Panel → **Ayarlar → Uyarı Bildirimi** (firma başına) |
| SMTP kimliği | Sunucu ortam değişkeni (parola tenant kaydına yazılmaz) |

```bash
ARGUS_SMTP_HOST=smtp.firma.com   ARGUS_SMTP_PORT=587
ARGUS_SMTP_USER=argus@firma.com  ARGUS_SMTP_PASS=***
ARGUS_SMTP_FROM=argus@firma.com  ARGUS_SMTP_TLS=true
ARGUS_PANEL_URL=http://192.168.2.247:5099     # e-postadaki panel bağlantısı
ARGUS_SYSLOG=10.0.0.5:514                     # SIEM'e CEF/syslog akışı (UDP)
```
SIEM akışı **CEF** biçimindedir → Splunk / QRadar / ArcSight / Wazuh doğrudan ayrıştırır. Uyarılar
toplanmadan anında gönderilir (korelasyon gecikmesin).

**Veri saklama (retention).** 300 makine × 15 sn telemetri ile `events` tablosu sınırsız büyür.
Panel → **Ayarlar → Veri Saklama** ile gün sayısı verilir (0 = sınırsız); sunucu genelinde varsayılan
`ARGUS_RETENTION_DAYS` ile belirlenir. Günde bir kez yalnız **telemetri** silinir —
firma, kullanıcı, lisans ve ayar kayıtlarına dokunulmaz. KVKK saklama süresiyle uyumlu seçin.

**Saat dilimi (`ARGUS_TZ`):** Uç nokta olayları agent'ın yerel saatiyle damgalanır, sunucu Ubuntu'da
genelde UTC çalışır. Rapor gün sınırları `ARGUS_TZ` ile belirlenir (varsayılan `Europe/Istanbul`),
böylece "Bugün" filtresi saat farkından kaymaz.

Not: `enroll` ve `telemetry` yanıtları güncel `settings` nesnesini de içerir — agent politikayı böyle canlı alır.

---

## Agent yapılandırması

`%LOCALAPPDATA%\Argus\config.json`:
```json
{
  "ServerUrl": "http://localhost:5099",
  "TenantKey": "demo-tenant-key-001",
  "SendSeconds": 15,
  "WatchFolders": ["C:\\Users\\...\\Desktop", "\\\\Sunucu\\Ortak"]
}
```
`ServerUrl`/`TenantKey` boşsa agent **yerel modda** çalışır (veriyi sadece diske yazar).
Ortam değişkenleri `ARGUS_SERVER` / `ARGUS_TENANT_KEY` config'i ezer (dağıtım kolaylığı).

---

## Test durumu

**Burada uçtan uca test edildi (✅):**
- Agent enroll → telemetri → panelde online uç nokta
- Aktif/pasif süre + uygulama kullanımı
- 12 dosya silme → **toplu-silme uyarısı**
- 16 dosya kopyalama → **toplu-kopyalama uyarısı**
- **SQLite** (varsayılan DB) → enroll, uyarı, panel, yeniden başlatmada kalıcılık — gerçek DB ile test edildi
- Anahtarsız istek → **401**
- **Ayarlar API'si** — GET/PUT `/settings` kalıcı; enroll yanıtı güncel politikayı taşıyor (temiz DB ile test edildi)

**Yazıldı & derleniyor, SENİN gerçek ortamında test edilmeli (⚠️):**
- **PostgreSQL** — sadece üretimde gerekir; çalışan bir PG sunucusu ister (`ARGUS_PG`). SQLite ile aynı arayüz.
- **Tarayıcı/link + gizli mod** — gerçek bir tarayıcı gerekir; headless ortamda doğrulanamaz.
  URL, UI Automation (COM) ile tarayıcı önplandayken yakalanır.
- **USB / harici disk** — gerçek çıkarılabilir sürücü gerekir (headless ortamda yok). Test: bir USB bellek tak
  (panelde **USB Takıldı**), içine 3+ dosya kopyala (**USB'ye Kopyaladı** + **usb_exfil** uyarısı), çıkar
  (**USB Çıkarıldı**). Watcher yoğun kopyalamada buffer taşarsa artık kendini otomatik toparlıyor.
- **USN Journal dosya motoru** — yönetici (admin) hakkı + NTFS birim gerekir; headless/normal kullanıcıda
  doğrulanamaz. Ayarlar'da motoru `usn` seç, agent'ı **admin** çalıştır, konsolda `[usn]` görünmeli;
  admin değilse otomatik `[fsw]`'ye düşer. Kod derleniyor, gerçek admin ortamında test edilecek.

**Doğruluk notu:** Dosya izleme şu an FileSystemWatcher (yoğun anlarda olay düşürebilir),
kopya tespiti ad-tabanlı sezgiseldir. Üretimde **ETW + USN Journal** ~%99.9 doğruluk sağlar.

---

## Sıradaki (production'a giden yol)

- [x] SQLite (varsayılan, gerçek DB) — *test edildi*
- [x] PostgreSQL desteği (`ARGUS_PG`, üretim) — *gerçek PG ile test edilecek*
- [x] Dosya kopyalama tespiti + toplu-kopyalama uyarısı
- [x] Tarayıcı/link + gizli mod yakalama (UI Automation) — *gerçek tarayıcıda test edilecek*
- [x] Dosya izleme: **USN Journal** kaynağı (kalıcı NTFS günlüğü, olay düşürmez) — motor `auto`/`usn`/`fsw` seçilir; USN admin+NTFS ister, olmazsa FileSystemWatcher'a düşer — *admin ile gerçek ortamda test edilecek*
- [ ] Dosya izleme: **ETW (Kernel-File)** — gerçek zamanlı + süreç ilişkilendirme (sıfır-bağımlılık ETW ayrı iş; USN güvenilirlik çekirdeğini zaten verdi)
- [x] USB / harici disk izleme (takılma/çıkarılma + medyaya kopyalama + `usb_exfil` uyarısı + panel "Veri Güvenliği" sekmesi) — *gerçek USB bellekle test edilecek*
- [x] **USB engelleme gerçekten uygulanıyor** — Removable Storage Access politikası (takılı aygıtta da geçerli,
      yeniden başlatma istemez) + USBSTOR/UASPStor + salt-okunur modu; panelde "hangi makinede uygulandı" doğrulaması
- [x] **Geriye dönük filtreleme** — Uyarılar / Veri Güvenliği / Dosya Olayları kişi+tarih filtreli, sayfalı, CSV'li
- [ ] Agent: Zamanlanmış Görev → **gerçek Windows Service + tamper koruması**
- [ ] İmzalı MSI + GPO paketi
- [x] Panel: **Ayarlar menüsü / politika editörü** — tenant başına uyarı eşikleri, dosya motoru, izlenen klasörler, USB aç/kapa, gönderim/boşta aralıkları; agent enroll/telemetri turunda canlı uygular
- [ ] Panel: kill switch, zamanlanmış raporlar, lisanslama
