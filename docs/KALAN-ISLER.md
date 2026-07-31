# Argus — Kalan İşler / Yol Haritası

Ana kod (kök) çalışır ve olgun durumda. Bu dosya **kalan işleri** ve her birinin
*nasıl yapılacağını* toplar. Bir maddeye sıra gelince buradan bakıp kökte uygularız.

Son güncelleme: 2026-07-18

---

## ✅ Biten (özet)
- **Agent:** aktif/boşta, uygulama süresi, dosya olayları (FSW + USN Journal), USB izleme,
  içerik sınıflandırma (TC/IBAN/kart/anahtar kelime), web yakalama (uzantı + UIA), web kategori/uyarı,
  dosya-içi (belge) süre, kaynak (nereden) takibi, uzaktan kill switch, gerçek Windows Service + tamper.
- **Backend:** multi-tenant + izolasyon, RBAC (superadmin/owner/admin/viewer), oturum, zorunlu parola değişimi,
  brute-force kilidi, CORS kapalı, politika/ayar, telemetri, tarih+departman filtreli raporlar (web/app/belge/olay),
  yönetici özeti, denetim günlüğü + otomatik yedek (SQLite), HTTP/HTTPS opsiyonel.
- **Panel:** giriş + zorunlu parola, role göre menü, Yönetim özeti, Aktivite (web/app/belge/rapor sekmeli),
  Dosya Olayları (açılır detay), Veri Güvenliği, Uyarılar, Agent Yönetimi (kill switch + departman + indirme paketi),
  Ayarlar, Kullanıcılar (departman), Firmalar, Sistem Günlüğü.
- **Dağıtım:** install-agent.ps1 (görev), install-service.ps1 (servis+tamper), install-webhost.ps1 (uzantı köprüsü),
  start-server(-lan/-https).ps1, make-cert.ps1, build-agent-package.ps1 (indirilebilir ZIP).

---

## 🔎 DLP KAPSAM İNCELEMESİ (2026-07-31) — nereden sızabilir, neyi görüyoruz?

Sahada (Arhan) çalışan sürüm incelendi. **Kapalı kanallar** ve **açık kalan kanallar**:

| Sızıntı kanalı | Durum | Not |
|---|---|---|
| USB bellek / harici disk | ✅ görülüyor + ✅ **engellenebiliyor** | takılma/çıkarma, medyaya kopyalama, `usb_exfil`; politika: serbest/salt-okunur/engelli |
| Telefon (MTP/WPD) | ⚠️ **engelleniyor, görülmüyor** | USB politikası WPD sınıfını da kapatır; ama MTP kopyalama **olayı** yakalanmıyor (birim harfi yok → FSW takılamaz) |
| Ağ paylaşımı (`\\filesrv`) | ✅ görülüyor | fileserver denetim agent'ı, gerçek kullanıcı adıyla |
| Yerel dosya sil/kopyala/değiştir | ✅ görülüyor | USN Journal / FSW |
| Web · gizli mod | ✅ görülüyor | uzantı + UIA; **hangi dosyanın yüklendiği** görünmüyor |
| Hassas içerik (TC/IBAN/kart) | ✅ görülüyor | doğrulamalı, düşük yanlış-pozitif |
| CD/DVD yazma | ✅ engelleniyor | politika `Deny_Write` |
| **Yazdırma (kâğıda)** | ❌ **yok** | klasik DLP kalemi — hangi belge, kaç sayfa, hangi yazıcı |
| **E-posta eki / webmail yükleme** | ❌ **yok** | "hangi dosya gitti" ilişkilendirmesi yok |
| **Bulut yükleme (Drive/WeTransfer/Dropbox)** | ❌ **yok** | site süresi görünür, yüklenen dosya görünmez |
| **Pano (kopyala-yapıştır) / ekran görüntüsü** | ❌ yok | |
| **Bluetooth / AirDrop** | ❌ yok | |

### İçerik taraması — ✅ KAPATILDI (2026-07-31)
- ✅ **PDF** taranıyor (`TextExtract.Pdf`: FlateDecode + Tj/TJ metin işleçleri). Gerçek 975 KB PDF: 323 ms.
- ✅ **Eski Office (.doc/.xls/.ppt/.msg)** — ASCII + UTF-16LE dizi çıkarımıyla taranıyor.
- ✅ **ZIP arşivi içi** özyinelemeli taranıyor → "zip'le ve USB'ye at" yolu kapandı.
- ✅ **Boyut tavanı** politikadan geliyor (varsayılan 32 MB, 512 MB'a kadar).
- ✅ **Parola korumalı / açılamayan** dosya (rar, 7z, şifreli zip) sızıntı sinyali sayılıyor.
- ⚠️ **Taranmış (görüntü) PDF** hâlâ kapsam dışı → OCR gerekir. Sıfır-bağımlılık kuralıyla çelişir;
  gerekirse ayrı bir OCR servisi olarak düşünülmeli.
- ⚠️ **RAR/7z içeriği** açılmıyor (tescilli format) — yalnız işaretleniyor.

**Bu sırada yakalanan iki gerçek hata:** (1) Türkçe I/İ/ı/i katlaması yapılmadığı için politikaya
"gizli" yazınca belgedeki "GİZLİ" kaçıyordu. (2) Kredi kartı tespiti yalnız Luhn'a bakıyordu →
rastgele 16 hanelinin ~%10'u geçiyor; roman/taslak PDF'lerinde bile "Kredi Kartı" bulgusu çıkıyordu.
Artık kart ailesi ön eki (Visa/MasterCard/Amex/Troy/UnionPay/Discover/JCB/Diners) de şart.

### Kurumsal işletim — ✅ KAPATILDI (2026-07-31)
- ✅ **Uyarı bildirimi**: e-posta (SMTP) + webhook (Slack/Teams) + **SIEM'e CEF/syslog** akışı.
  Toplu gönderim + tip başına bekleme ile uyarı yağmuru engelleniyor.
- ✅ **Veri saklama**: firma başına gün sayısı, günde bir otomatik temizlik; yalnız telemetri silinir.

### Diğer teknik borçlar (açık)
- **Kopya tespiti ad-tabanlı sezgisel** → yeniden adlandırılan kopya kaçar; içerik hash'i (SHA-256) ile eşleştirilmeli.
- **Süreç ilişkilendirme yok** (ETW) → "hangi uygulama kopyaladı" bilinmiyor.
- **Zamanlanmış raporlar** (haftalık PDF/CSV e-postası) yok.
- **AD/LDAP ile giriş** yok — panel kullanıcıları elle açılıyor.
- ✅ **Otomatik test** altyapısı kuruldu (xUnit, 45 test): içerik sınıflandırıcı + depolama + geriye dönük sorgular.
  ⚠️ Geliştirme makinesinde Windows **Smart App Control** imzasız `Argus.Server.dll`'i yüklemiyor →
  backend testleri yalnız CI'da koşuyor.

---

## 🔴 KALAN — sırayla

### 1. PostgreSQL'e geçiş (üretim ölçeği)
**Durum:** Kod HAZIR — `PostgresStore` tam, `ARGUS_PG` verilince otomatik devreye girer. Sadece gerçek PG ile test edilmedi.
**Neden:** SQLite tek dosya/tek yazar; 300 makine × 15 sn telemetri onu zorlar. Postgres kaldırır.
**Nasıl:**
1. PostgreSQL kur (Docker en kolay):
   ```
   docker run -d --name argus-pg -e POSTGRES_PASSWORD=argus -e POSTGRES_DB=argus -p 5432:5432 postgres:16
   ```
2. Sunucuyu bağlan:
   ```
   $env:ARGUS_PG = "Host=localhost;Port=5432;Database=argus;Username=postgres;Password=argus"
   deploy\start-server.ps1
   ```
   Şema otomatik oluşur (InitSchema). Panel/agent aynı çalışır.
3. Test: enroll + telemetri + panel + rapor + firma oluştur → izolasyon.
4. **Yedek:** SQLite otomatik yedeği PG'de geçerli değil → cron ile `pg_dump`:
   ```
   pg_dump -h localhost -U postgres argus > argus-$(date).sql
   ```
**Dikkat:** Bağlantı dizesindeki şifre; üretimde `.env`/secret store. TLS'li PG bağlantısı önerilir.

### 2. Self-contained agent + imzalı MSI (dağıtım)
**Self-contained — ✅ BİTTİ:** `build-agent-package.ps1` artık agent+webhost'u **tek-exe self-contained** (runtime gömülü) yayınlıyor → hedefte **.NET GEREKMEZ**. Paketin `install.ps1`'i **gizli Windows Service + tamper** olarak kurar. ZIP ~57 MB, panelden indirilir (`/download/argus-agent.zip`). Test: tek exe standalone çalışıyor.
**MSI — bekliyor:**
1. **MSI:** WiX Toolset (v4) ile `.wxs` yaz → self-contained exe + install-service mantığını MSI'ya göm.
   Alternatif: hazır installer (Inno Setup / Advanced Installer).
3. **Kod imzalama:** MSI + exe'yi **kod imzalama sertifikası** ile imzala (Authenticode).
   Sertifika bir CA'dan alınır (~yıllık ücret). İmzasız paket SmartScreen/AV takılır.
4. **GPO dağıtımı:** İmzalı MSI'yi GPO "Software Installation" ile tüm makinelere it.
**Bağımlılık:** WiX/Inno kurulumu + kod imzalama sertifikası (dışarıdan).

### 3. Tarayıcı eklentisi otomatik dağıtım
**Chrome/Edge — ✅ BİTTİ (self-hosted, Web Store'suz):**
- `deploy\build-extension.ps1` → Chrome ile CRX paketler, **sabit ID** hesaplar (CRX header'dan), CRX+ID'yi `backend\wwwroot\ext\`'e koyar. (İmzalama anahtarı `deploy\argus-ext.pem` — SAKLA/git'e koyma.)
- Sunucu barındırır: `/ext/update.xml` (dinamik codebase) + `/ext/argus-ext.crx`.
- `deploy\install-extension-policy.ps1 -ServerUrl http://SUNUCU:5099` → Chrome+Edge `ExtensionInstallForcelist` = `<ID>;<update_url>` (HKLM). Sahada aynı anahtarı **GPO** ile dağıt.
- Webhost köprüsü: `install-webhost.ps1 -ExtensionId nchclcmnnndflmefplbjbggdoekihkin` (sabit ID). Üretimde HKLM'e (makine geneli) taşınmalı.
- **Dikkat:** Self-hosted force-install **domain/managed makinede** (GPO senaryosu) sorunsuz. Standalone test makinesinde Chrome "Web Store dışı" korumasıyla engelleyebilir — bu bir hata değil, domain'de çalışır.

**Firefox — ✅ ESR yapıldı / release imza bekliyor:**
- `extension-firefox\manifest.json` (gecko id `argus@argus.local` + background.scripts) → build-extension.ps1 **XPI** üretir → sunucu `/ext/argus-ext.xpi`.
- `deploy\install-firefox-policy.ps1 -ServerUrl ...` → Firefox `policies.json` (force_installed + install_url) + native host (allowed_extensions) + **ESR'de imza şartını kapatır** → imzasız XPI kurulur.
- **Firefox ESR:** ÇALIŞIR (imzasız). **Normal/release Firefox:** imza şartı kapatılamaz → XPI **AMO imzalı** olmalı: Mozilla hesabı + `web-ext sign --api-key ... --api-secret ...` → imzalı XPI'yi barındır, install_url'i ona çevir. (Kurumsal genelde ESR kullanır.)

### 4. Lisanslama (satış modeli) — ✅ BİTTİ
- `tenants.seat_limit` (0=sınırsız) + `tenants.expires_at` (null=süresiz) sütunları.
- **Enroll enforcement:** süre dolduysa VE/VEYA koltuk limiti doluysa (yeni makine) → 403 reddedilir; mevcut makineler serbest. (Program.cs enroll)
- **Panel (Firmalar, süper-admin):** her firmada Lisans butonu → koltuk limiti + bitiş tarihi; listede kullanım (agent/limit) + bitiş + "SÜRESİ DOLDU" rozeti. `PUT /tenants/{id}/license`.
- Test edildi: dolmuş süre→403, koltuk dolu+yeni makine→403, mevcut makine/limit yok→200.
- (İleride: imzalı offline lisans dosyası istenirse eklenebilir; şu an sunucu-tarafı doğrulama yeterli.)

### 5. Otomatik test
**Durum:** Yok (her şey elle doğrulandı).
**Nasıl:** `Argus.Tests` (xUnit) → ContentClassifier (TC/IBAN/Luhn), AlertEngine eşikleri,
Passwords hash/verify, store round-trip (SQLite in-memory), izolasyon/RBAC endpoint testleri (WebApplicationFactory).

### 6. KVKK şablonları (hukuki)
**Durum:** Panelde "KVKK aktif" göstergesi var ama şablon yok.
**Nasıl:** Ürüne aydınlatma metni + çalışan izleme politikası + onay/imza kaydı (dosya) ekle.
Panelde tenant başına "politika yüklendi/onaylandı" durumu.

### 7. ETW (süreç ilişkilendirme)
**Durum:** USN Journal var (güvenilir dosya olayı) ama "hangi uygulama kopyaladı" yok.
**Nasıl:** ETW Kernel-File sağlayıcısını dinle → dosya olayını süreçle eşle. Büyük iş; USN yeterli değilse.

---

## Notlar
- **"Burada admin yok"** kısıtı: Service/MSI/GPO/PG kurulumları admin'li test makinesi ister. Kod yazılır, orada test edilir.
- Kök kod stabil; değişiklik gerektiğinde ilgili maddeye göre `Desktop\Argus-DLP\` içinde uygulanır.
- Mimari genel bakış: `docs\MIMARI.md`.
