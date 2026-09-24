// Rasterize deepseek-color.svg into multiple sizes and pack a Windows .ico
// whose entries are PNG-compressed images (Vista+ supported).
const sharp = require('sharp');
const fs = require('fs');
const path = require('path');

const svgPath = path.join(__dirname, '..', 'deepseek-color.svg');
const outPath = path.join(__dirname, '..', 'app.ico');
const SIZES = [16, 24, 32, 48, 64, 128, 256];

(async () => {
  const svg = fs.readFileSync(svgPath);

  // Render at high density once, then downscale per entry.
  const masterPng = await sharp(svg, { density: 72 * (1024 / 24) })
    .resize(1024, 1024, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .png()
    .toBuffer();

  const entries = [];
  for (const size of SIZES) {
    const png = await sharp(masterPng)
      .resize(size, size, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
      .png()
      .toBuffer();
    entries.push({ size, png });
  }

  // ICO header
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);           // reserved
  header.writeUInt16LE(1, 2);           // type: icon
  header.writeUInt16LE(entries.length, 4);

  const dirEntries = Buffer.alloc(16 * entries.length);
  let offset = 6 + 16 * entries.length;
  const blobs = [];
  entries.forEach((e, i) => {
    const o = i * 16;
    dirEntries.writeUInt8(e.size >= 256 ? 0 : e.size, o);      // width (0 = 256)
    dirEntries.writeUInt8(e.size >= 256 ? 0 : e.size, o + 1);  // height
    dirEntries.writeUInt8(0, o + 2);                           // palette
    dirEntries.writeUInt8(0, o + 3);                           // reserved
    dirEntries.writeUInt16LE(1, o + 4);                        // planes
    dirEntries.writeUInt16LE(32, o + 6);                       // bpp
    dirEntries.writeUInt32LE(e.png.length, o + 8);             // data size
    dirEntries.writeUInt32LE(offset, o + 12);                  // data offset
    blobs.push(e.png);
    offset += e.png.length;
  });

  fs.writeFileSync(outPath, Buffer.concat([header, dirEntries, ...blobs]));
  console.log(`wrote ${outPath} (${fs.statSync(outPath).size} bytes, ${entries.length} sizes)`);
})();
