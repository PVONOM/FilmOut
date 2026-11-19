// Example camera controller for FilmOut.
//
// For each frame: wait until FilmOut is ARMED, open the shutter, send NEXT, wait for OFF,
// close the shutter, advance the film, repeat. The shutter is open only while the image is
// up (plus the settle times), so screen glow between exposures doesn't reach the film.
//
// Pins and timings are placeholders: adapt them to your camera's shutter and advance controls.

const int SHUTTER_PIN = 7;        // HIGH opens the shutter (e.g. via a relay or opto-isolator)
const int ADVANCE_PIN = 8;        // a pulse advances one frame
const int START_BUTTON_PIN = 2;   // to ground; press to start or pause the sequence

const unsigned long SHUTTER_OPEN_SETTLE_MS = 150;   // time for the shutter to open fully
const unsigned long SHUTTER_CLOSE_SETTLE_MS = 150;
const unsigned long ADVANCE_PULSE_MS = 80;
const unsigned long ADVANCE_SETTLE_MS = 600;        // film settles in the gate

bool running = false;
bool armed = false;
String line;

void setup() {
  pinMode(SHUTTER_PIN, OUTPUT);
  pinMode(ADVANCE_PIN, OUTPUT);
  pinMode(START_BUTTON_PIN, INPUT_PULLUP);
  digitalWrite(SHUTTER_PIN, LOW);
  digitalWrite(ADVANCE_PIN, LOW);
  Serial.begin(115200);
}

// Reads one reply from FilmOut, if a whole line has arrived.
bool readLine(String &out) {
  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (line.length() > 0) { out = line; line = ""; return true; }
    } else if (line.length() < 80) {
      line += c;
    }
  }
  return false;
}

void exposeOneFrame() {
  digitalWrite(SHUTTER_PIN, HIGH);
  delay(SHUTTER_OPEN_SETTLE_MS);
  Serial.println("NEXT");

  // Wait for OFF (or an error), however long the exposure is.
  String reply;
  while (true) {
    if (readLine(reply)) {
      if (reply.startsWith("OFF")) break;
      if (reply.startsWith("ERR")) { digitalWrite(SHUTTER_PIN, LOW); running = false; return; }
    }
  }

  delay(SHUTTER_CLOSE_SETTLE_MS);   // margin: the black frame is already on screen when OFF arrives
  digitalWrite(SHUTTER_PIN, LOW);
  delay(SHUTTER_CLOSE_SETTLE_MS);

  digitalWrite(ADVANCE_PIN, HIGH);
  delay(ADVANCE_PULSE_MS);
  digitalWrite(ADVANCE_PIN, LOW);
  delay(ADVANCE_SETTLE_MS);
}

void loop() {
  if (digitalRead(START_BUTTON_PIN) == LOW) {
    running = !running;
    delay(300);
    if (running) Serial.println("STATUS?");
  }

  String reply;
  if (readLine(reply)) {
    if (reply.startsWith("ARMED") || reply.startsWith("STATUS ARMED")) armed = true;
    if (reply.startsWith("DONE")) running = false;
  }

  if (running && armed) {
    armed = false;
    exposeOneFrame();
  }
}
