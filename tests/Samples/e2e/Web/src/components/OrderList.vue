<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getOrder, type Order } from '../api'

const props = defineProps<{ orderId: number }>()
const order = ref<Order | null>(null)

onMounted(async () => {
  order.value = await getOrder(props.orderId)
})
</script>

<template>
  <div v-if="order">Order {{ order.orderId }}: {{ order.total }}</div>
</template>
