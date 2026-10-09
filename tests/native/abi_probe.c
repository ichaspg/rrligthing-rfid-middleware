#include <stddef.h>
#include <stdio.h>
#include "../../linux/x86_64/release/rfid.h"

int main(void) {
  printf("size=%zu\n", sizeof(auto_read_output));
  printf("ant=%zu\n", offsetof(auto_read_output, ant));
  printf("fin_count=%zu\n", offsetof(auto_read_output, fin_count));
  printf("rssi=%zu\n", offsetof(auto_read_output, rssi));
  printf("ts=%zu\n", offsetof(auto_read_output, ts));
  printf("temperature=%zu\n", offsetof(auto_read_output, temperature));
  printf("longitude=%zu\n", offsetof(auto_read_output, longitude));
  return 0;
}
