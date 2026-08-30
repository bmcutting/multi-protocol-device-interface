# DeviceChannel

Implementación de referencia del artículo *Una interfaz para hablar con todos
los protocolos*: un contrato único de comunicación con dispositivos de campo,
con dos adaptadores debajo —Modbus TCP y MQTT— que no se parecen en nada entre
sí.

Quien consume los datos programa contra `IDeviceChannel` y recibe siempre un
`Reading` con el mismo tipo de valor, sin saber qué protocolo hay debajo.
Añadir un protocolo nuevo es implementar la interfaz una vez más, sin tocar
los adaptadores existentes ni el código que consume los datos.

## Proyectos

| Proyecto | Contenido |
| --- | --- |
| `DeviceChannel.Abstractions` | El contrato: `IDeviceChannel`, `Reading`, `DeviceData`, `Result`. Sin dependencias. |
| `DeviceChannel.Modbus` | Adaptador Modbus TCP sobre NModbus. |
| `DeviceChannel.Mqtt` | Adaptador MQTT sobre MQTTnet. |
| `DeviceChannel.Configuration` | Construye la instalación desde un archivo JSON. |
| `DeviceChannel.Demo` | Consumidor que usa ambos canales sin una sola línea específica de protocolo. |
| `DeviceChannel.TestSlave` | Esclavo Modbus TCP en memoria para probar sin hardware. |

## Probar la interfaz

La demostración es una habitación de hospital con cinco dispositivos repartidos
entre los dos protocolos, según lo que cada uno hace bien:

| Dispositivo | Protocolo | Por qué |
| --- | --- | --- |
| Temperatura de la habitación | Modbus | Sonda cableada del climatizador |
| Consigna del termostato | Modbus | Se lee y se escribe |
| Luz | Modbus | Una bobina: encendida o apagada |
| Ocupación de la cama | MQTT | Sensor inalámbrico que avisa al cambiar |
| Humedad | MQTT | Sensor inalámbrico que publica periódicamente |

Sobre ellos, un menú con las cinco operaciones del contrato:

```
    1) Connect          (ConnectAsync)
    2) Disconnect       (DisconnectAsync)
    3) Read the room    (ReadAsync)
    4) Write a value    (WriteAsync)
    5) Watch for 20 s   (SubscribeAsync)
```

Hay dos formas de ejecutarla: con la instalación simulada dentro del propio
proceso, o contra un esclavo Modbus y un broker MQTT que ya estén corriendo.

---

## A. Todo simulado

Requiere .NET 9 y un broker MQTT en `127.0.0.1:1883`. El esclavo Modbus y los
sensores inalámbricos los levanta la propia demostración.

```bash
dotnet run --project DeviceChannel.Demo
```

Si no tienes broker, con Docker:

```bash
docker run -it --rm -p 1883:1883 eclipse-mosquitto:2   mosquitto -c /mosquitto-no-auth.conf
```

Sin broker la demostración sigue siendo útil: la parte Modbus funciona igual y
los canales MQTT devuelven el fallo de conexión por `Result`, no por excepción.

### Recorrido recomendado

**`1` — Conectar.** Los dos canales responden por separado. Hasta hacerlo, las
lecturas fallan.

**`3` — Leer la habitación.** Los cinco dispositivos con el mismo formato, sin
que el código del menú sepa de qué protocolo viene cada uno:

```
  Room temperature       22,1 °C        [Modbus]  (read at 16:50:35)
  Thermostat setpoint    21,0 °C        [Modbus]  (read at 16:50:35)
  Room light             off            [Modbus]  (read at 16:50:35)
  Bed 302-A              occupied       [MQTT]    (read at 16:50:35)
  Room humidity          45,0 %         [MQTT]    (read at 16:50:35)
```

La temperatura llega como número aunque Modbus transporte dos registros de 16
bits sin decir qué significan. Si un dispositivo MQTT aún no ha publicado nada,
aparece como `no data yet`: no es un error, porque el canal funciona y lo
único que falta es que alguien publique.

**`4` — Cambiar algo.** Elige el termostato y escribe `26`. La demostración
relee el valor para comprobarlo, y en las siguientes lecturas la temperatura de
la habitación se va acercando a la nueva consigna.

**`3` otra vez.** La temperatura ha subido. La escritura tuvo efecto.

**`5` — Vigilar.** Se suscribe a los cinco durante veinte segundos. Es donde
mejor se ve la asimetría entre protocolos:

```
  [16:51:33] Room temperature       22,1 °C        [Modbus]
  [16:51:35] Room temperature       21,5 °C        [Modbus]
  [16:51:33] Bed 302-A              occupied       [MQTT]  (unchanged)
  [16:51:38] Room humidity          45,7 %         [MQTT]
  [16:51:48] Bed 302-A              free           [MQTT]
```

Modbus no tiene avisos: el adaptador pregunta cada dos segundos y solo emite
cuando el valor ha cambiado. MQTT sí avisa, y cuando pasan dos segundos sin
noticias el canal repite el último valor conocido —marcado como
`(unchanged)`, porque su `Timestamp` no ha cambiado— para que el silencio de
un sensor no se confunda con normalidad.

**`2` y luego `3`.** Con los canales cerrados, las lecturas pasan a
`Result.Failure` con el motivo. Es cosa distinta de no tener dato.

---

## B. Contra un esclavo y un broker reales

Si ya tienes corriendo un simulador Modbus —ModRSsim2, Diagslave, un PLC— y un
broker Mosquitto, la demostración puede usarlos en lugar de los simulados:

```bash
dotnet run --project DeviceChannel.Demo -- --modbus 127.0.0.1:502 --mqtt 127.0.0.1:1883
```

`--modbus` desactiva el esclavo simulado y `--mqtt` desactiva los sensores
simulados, de modo que se pueden combinar por separado:

```bash
# Esclavo real, sensores MQTT simulados
dotnet run --project DeviceChannel.Demo -- --modbus 127.0.0.1:502

# Esclavo simulado, broker real sin sensores simulados
dotnet run --project DeviceChannel.Demo -- --mqtt 127.0.0.1:1883
```

La cabecera indica en todo momento qué se está usando:

```
  Modbus  127.0.0.1:502 unit 1   (real)
  MQTT    127.0.0.1:1883 hospital/room302/#   (not simulated)
```

### Ajustar las direcciones

Por defecto la demostración espera la temperatura en el registro 0, la consigna
en el 2 y la luz en la bobina 0, con el esclavo número 1. Si tu dispositivo usa
otras, se indican al arrancar:

```bash
dotnet run --project DeviceChannel.Demo --   --modbus 192.168.1.50:502 --unit 3 --temperature 100 --setpoint 102 --light 5
```

`dotnet run --project DeviceChannel.Demo -- --help` lista todas las opciones.

### Publicar a mano en MQTT

Con `--mqtt` no hay sensores simulados, así que los dos dispositivos
inalámbricos aparecen como `no data yet` hasta que alguien publique. Desde
otra terminal:

```bash
mosquitto_pub -h 127.0.0.1 -t hospital/room302/bed/occupied -m true -r
mosquitto_pub -h 127.0.0.1 -t hospital/room302/humidity -m 47.2 -r
```

En Windows, si `mosquitto_pub` no está en el PATH, la ruta completa suele ser
`"C:\Program Files\mosquitto\mosquitto_pub.exe"`.

La opción `-r` marca el mensaje como retenido, de modo que el broker se lo
entregue también a quien se suscriba después. Sin ella, un valor publicado antes
de que la demostración se conecte se pierde: en MQTT, lo que no se escucha en su
momento no se puede leer luego.

Con la demostración vigilando (`5`), cada publicación aparece de inmediato. Es
la diferencia con Modbus, donde el valor solo se descubre en el siguiente
sondeo.

### El esclavo de pruebas por separado

También se puede ejecutar solo, para apuntar contra él desde otro programa:

```bash
dotnet run --project DeviceChannel.TestSlave 5020
```

## Definir los dispositivos

Los dispositivos no están escritos en el código: se declaran en
`DeviceChannel.Demo/installation.json`. Añadir uno es editar ese archivo, sin
recompilar nada.

```json
{
  "sources": [
    { "name": "wired",    "protocol": "modbus", "endpoint": "tcp://192.168.1.50:502" },
    { "name": "wireless", "protocol": "mqtt",   "endpoint": "127.0.0.1:1883",
      "topicFilters": [ "planta/#" ] }
  ],

  "data": [
    {
      "name": "Sensor 2 - Temperatura",
      "source": "wired",
      "device": "Sensor 2",
      "unit": "°C",
      "unitId": 2,
      "registerType": "HoldingRegister",
      "startAddress": 0,
      "dataType": "Float32",
      "access": "ReadOnly"
    },
    {
      "name": "Humedad nave",
      "source": "wireless",
      "unit": "%",
      "topic": "planta/nave/humedad",
      "payloadType": "Number"
    }
  ]
}
```

`sources` son los orígenes —un esclavo Modbus, un broker MQTT— y se declaran una
sola vez. `data` son los datos que se leen de ellos, y cada uno referencia su
origen por nombre.

| Campo | Aplica a | Para qué |
| --- | --- | --- |
| `name` | todos | Nombre con el que aparece en la aplicación |
| `source` | todos | Origen del que se lee |
| `device` | todos | Dispositivo al que pertenece, para agrupar en la vista |
| `unit` | todos | Unidad de medida, solo para presentación |
| `access` | todos | `ReadWrite` por defecto, o `ReadOnly` |
| `unitId`, `registerType`, `startAddress`, `dataType`, `wordOrder` | Modbus | Dónde está el dato y cómo se interpreta |
| `topic`, `payloadType` | MQTT | Tema y formato del contenido |

### `access` recoge lo que el protocolo no puede decir

Modbus ya impide escribir en un `InputRegister` o un `DiscreteInput`: son de
solo lectura por definición del protocolo. Pero un `Coil` que la instalación no
permite tocar —un enclavamiento, un relé de seguridad— es escribible para
Modbus y no debe serlo para la aplicación.

Esa restricción no viaja en ninguna trama, así que se declara en el contrato con
`access`, y el adaptador la hace cumplir antes de llegar al enlace. Lo mismo
ocurre con una sonda: la temperatura de la habitación está en un registro que
Modbus deja escribir, y aun así escribirla no significa nada.

### Añadir un protocolo

Todo el conocimiento de protocolos vive en `InstallationLoader`, en un `switch`
de dos ramas que decide qué adaptador construir para cada origen. Sumar OPC UA
es añadir una rama ahí y su implementación de `IDeviceChannel`. El código que
consume los datos no se toca.

## Cada protocolo trae media interfaz

Ninguno de los dos implementa el contrato entero. Cada uno trae una mitad y
obliga a construir la otra:

| | Modbus TCP | MQTT |
| --- | --- | --- |
| Nativo | Lectura bajo demanda | Notificación por evento |
| Fabricado | Suscripción, con sondeo y comparación | Lectura, con caché del último valor |
| `ReadAsync` significa | El estado actual del dispositivo | El último valor recibido |
| Idempotente | Sí | Sí, pero puede no haber valor |
| Precio | El sondeo compite con las lecturas por el enlace | Un dato sin publicar no se puede leer |

Esa asimetría es el motivo de las decisiones siguientes.

## Decisiones de diseño

### El canal no juzga la vigencia del valor

`ReadAsync` no significa lo mismo en los dos adaptadores: en Modbus interroga al
esclavo y en MQTT devuelve lo último que llegó, que puede ser de hace cinco
minutos.

Esa diferencia no se anuncia en el canal sino en cada valor: `Reading` lleva
`Timestamp`. Pero el canal no decide si ese valor es viejo, porque no puede
saberlo —veinte segundos son aceptables para una temperatura y no lo son para
un enclavamiento—. Entrega el hecho y el consumidor aplica su propio criterio.

### Que no haya dato no es un fallo

En MQTT, leer un dato del que aún no ha llegado ninguna publicación no es un
error: el broker responde, el dispositivo puede estar sano, y lo único que falta
es que alguien publique. Devolverlo como fallo lo haría indistinguible de un
enlace roto.

`Reading.HasValue` distingue los dos casos y el fallo real queda para
`Result.Failure`.

### La caché MQTT no descarta el valor al leerlo

Vaciar la entrada tras entregarla resuelve un problema —saber si un valor es
nuevo— y crea uno peor: dos lecturas seguidas devuelven cosas distintas sin que
nada haya cambiado en planta, y `ReadAsync` deja de ser idempotente justo en el
adaptador donde el consumidor no lo espera.

Aquí la caché conserva el valor y la novedad se resuelve con
`Reading.Timestamp`, que es información y no un efecto secundario.

### El valor llega interpretado, no en crudo

Modbus transporta registros de 16 bits y no dice qué significan: que dos
registros consecutivos sean un `Float32` o un entero con signo está en el manual
del fabricante, no en el protocolo. Lo mismo ocurre con el orden de las
palabras, que varía según el equipo.

Esa información falta en el protocolo, así que se declara en el contrato:
`ModbusDeviceData` lleva `DataType` y `WordOrder`, `MqttDeviceData` lleva
`PayloadType`, y el adaptador entrega un `double`, un `bool` o un `string`. El
consumidor recibe el mismo tipo venga de donde venga.

### `maxStaleness` en lugar de un período interno

La suscripción Modbus se fabrica con sondeo, y el período viene del parámetro
`maxStaleness`, no de una constante en el adaptador. El bucle descuenta lo que
tardó la propia lectura, de modo que el intervalo no derive con la latencia del
enlace.

En MQTT el mismo parámetro sirve para lo contrario: detectar silencio. Un
dispositivo que deja de publicar no genera ningún evento, y un consumidor que
solo escucha eventos no distingue eso de un proceso estable. Al vencer el plazo,
el canal reemite el último valor conocido.

### Los accesos Modbus se serializan

Modbus TCP sobre un mismo socket es estrictamente petición-respuesta. Como el
sondeo de la suscripción y las lecturas del consumidor comparten conexión, todas
las transacciones pasan por un `SemaphoreSlim`. Sin eso, las tramas se
entrelazarían sobre el enlace.

Es una consecuencia directa de fabricar el push: al no existir la suscripción
nativa, aparece un hilo de fondo que compite por el mismo recurso que el
consumidor.

## Añadir un protocolo

Implementar `IDeviceChannel` y una subclase de `DeviceData` con lo que ese
protocolo necesite para localizar un dato. Nada más: ni los adaptadores
existentes ni el código que consume los datos se tocan.

Al hacerlo, las preguntas que conviene contestar son las mismas que resolvieron
estos dos: qué mitad del contrato trae el protocolo, qué hay que fabricarle, qué
cuesta esa fabricación, y dónde queda escrito ese coste.
