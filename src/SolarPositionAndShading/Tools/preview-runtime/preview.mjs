import fs from 'node:fs/promises';
import path from 'node:path';
import { FileBlob, SpreadsheetFile } from '@oai/artifact-tool';
const folder=path.resolve(import.meta.dirname,'../../Validator/results');
for(const [file,sheet,range] of [
  ['shading-month.xlsx','Shading','A1:H14'],
  ['shading-month.xlsx','Method','A1:B24'],
  ['shading-month-solar-positions.xlsx','Solar positions','A1:C14'],
  ['shading-month-solar-positions.xlsx','Method','A1:B12']
]){
  const wb=await SpreadsheetFile.importXlsx(await FileBlob.load(path.join(folder,file)));
  const preview=await wb.render({sheetName:sheet,range,scale:1,format:'png'});
  await fs.writeFile(path.join(folder,`${file}-${sheet.replaceAll(' ','-')}-preview.png`),new Uint8Array(await preview.arrayBuffer()));
  console.log(`${file}: ${sheet} rendered`);
}
