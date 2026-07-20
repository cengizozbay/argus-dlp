# Argus — Endpoint Aktivite İzleme / DLP

Çalışan izleme (engelleme yok, sadece gözlem + uyarı). Referans: Safetica.
**Ürün modeli: satılacak, çok-müşterili (multi-tenant) ürün.** Her müşteri şirket = bir tenant.

## Onaylanan kararlar (2026-07-03)
- **Satılacak ürün** → multi-tenant mimari, tenant başına izolasyon, lisanslama, kolay kurulum.
- **Dağıtım: Active Directory / GPO** → imzalı MSI, `msiexec /qn` ile sessiz kurulum, makine bazlı.
- **Agent kullanıcıdan gizli çalışır** → arayüzsüz Windows Service, tepsi ikonu yok; yalnız **admin panelinden** devre dışı bırakılır/kaldırılır (uzaktan "kill switch").
- **Dil: C#/.NET 8.** Kodu Fable yazıyor.
- Dosya izleme: soyut `IFileEventSource`; MVP'de FileSystemWatcher, üretimde **ETW + USN Journal** (driver yok).
- Tarayıcı gizli mod: **UI Automation** ile adres çubuğu.

## Üç ana bileşen

### 1. Endpoint Agent (gizli Windows Service)
- Arayüzsüz, LocalSystem, otomatik başlar; kullanıcı görmez/durduramaz
- **Tamper koruması:** servis kurtarma (öldürülünce yeniden başlar), watchdog, ACL ile korumalı; kaldırma yalnız imzalı sunucu komutuyla
- Yakalananlar: önplan uygulaması + aktif/boşta [✓], dosya olayları [✓], tarayıcı (gizli dahil), USB/harici disk
- Yerel SQLite buffer (offline dayanıklılık) → tenant anahtarıyla HTTPS'ten sunucuya
- **Enrollment:** GPO ile kurulunca tenant anahtarıyla sunucuya kaydolur

### 2. Backend (multi-tenant SaaS)
- Tenant yönetimi + izolasyon + lisans
- Telemetri toplama API (ASP.NET Core)
- **Uyarı (kural) motoru** — kurallar tenant politikasından gelir [motor prototipi ✓]
- DB: ClickHouse/TimescaleDB (300+ makine/tenant hacmi)
- Barındırma: bulut multi-tenant (kurumsal müşteriye opsiyonel on-prem)

### 3. Admin Panel (dashboard) [prototip ✓]
- Tenant bazlı; kişi drill-down, web/dosya logları, uyarılar, en verimsiz personel
- Agent yönetimi: kill switch, politika düzenleme, dağıtım durumu

## Yol haritası
- **Faz 0/1** — aktif/boşta + uygulama takibi ....... ✓ çalışıyor & test edildi
- **Faz 2** — dosya olayları + toplu-silme uyarısı ... ✓ çalışıyor & test edildi
- **Faz 3** — tarayıcı (gizli mod, UIA/uzantı) + web süreleri ... ✓ çalışıyor
- **Faz 4** — gizli Windows Service + tamper koruması + MSI/GPO paketi ... ⏳ (admin gerektirir, ertelendi)
- **Faz 5** — backend: enrollment + telemetri + multi-tenant + SQLite→sunucu akışı ... ✓ çalışıyor & test edildi
- **Faz 6** — panelin gerçek veriye bağlanması + raporlar ... ✓ çalışıyor
- **Faz 6b** — USB/harici disk izleme (DLP) ... ✓ çalışıyor & test edildi
- **Faz 6c** — USN Journal dosya kaynağı (güvenilir, admin ister; yoksa FSW'ye düşer) + sunucu-tabanlı canlı politika (Ayarlar ekranı/kural editörü) ... ✓ çalışıyor & test edildi
- **Faz 7** — **hassas içerik sınıflandırma (DLP çekirdeği)**: dosya içeriği taranır → TC kimlik (sağlamalı), IBAN (mod-97), kredi kartı (Luhn), anahtar kelime; hassas dosya kopya/USB ile taşınınca `sensitive_exfil` kritik uyarısı ... ✓ çalışıyor & uçtan uca test edildi
- **Bekleyen** — lisanslama, dosya-içi süre (per-doküman), ETW (süreç ilişkilendirme)

## Hassas içerik taraması (Faz 7 · DLP çekirdeği)
- Agent: `ContentClassifier` — create/modify/copy/usb_copy olaylarında dosya içeriğini tarar.
  - Kapsam: metin (.txt/.csv/.json/.xml/.html/.md/.sql…) + Office (.docx/.xlsx/.pptx, ZIP→XML metni). ≤8 MB, kilitliyse atlar.
  - Doğrulamalı tespit (düşük yanlış-pozitif): TC kimlik sağlaması, IBAN mod-97, kart Luhn, politika anahtar kelimeleri.
  - `FileEvent.Sensitivity` etiketi olaya iliştirilir → panele akar; `AlertEngine` hassas kopya/USB'de `sensitive_exfil` üretir (eşik varsayılan 1).
- Backend: `events.sensitivity` sütunu (SQLite+PG, güvenli migrasyon), telemetride round-trip.
- Panel: Dosya Olayları'nda "Hassas" rozeti + filtresi; Veri Güvenliği'nde Hassas Dosya KPI'ı + "Hassas İçerik Taşıma" tablosu; Ayarlar'da içerik taraması aç/kapa + eşik + anahtar kelime listesi (tenant politikası, canlı uygulanır).

## KVKK / hukuki (ürünü satarken kritik)
Gizli (kullanıcıya bildirmeden) izleme Türkiye'de KVKK'ya ve iş hukukuna aykırı olabilir. Ürünü satarken:
- Müşteri şirkete **aydınlatma metni + çalışan politikası şablonu** sağlanmalı (ürünün parçası)
- Sorumluluk dağıtan şirkette; yine de "uyumlu kullanım" özelliği (politika onayı, denetim kaydı) ürünü satılabilir kılar
- Panelde "KVKK Politikası aktif" göstergesi bunun içindi
